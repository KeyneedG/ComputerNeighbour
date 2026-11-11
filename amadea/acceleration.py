import logging
import os
import sys

from .state import ROOT

LOG = logging.getLogger(__name__)


def enable(torch):
    runtime = ROOT / ".runtime"
    profile = runtime / "accelerators"
    triton = profile / "triton"
    compiler = triton / "runtime" / "tcc" / "tcc.exe"
    if not compiler.is_file() or torch.cuda.get_device_capability()[0] < 8:
        return
    if str(profile) not in sys.path:
        sys.path.append(str(profile))
    os.environ.setdefault("CC", str(compiler))
    os.environ.setdefault("CUDA_PATH", str(triton / "backends" / "nvidia"))
    os.environ.setdefault("TRITON_CACHE_DIR", str(runtime / "cache" / "triton"))
    os.environ.setdefault("TRITON_CACHE_AUTOTUNING", "1")
    try:
        from fla.ops.gated_delta_rule import chunk_gated_delta_rule, fused_recurrent_gated_delta_rule
        from transformers.models.qwen3_5 import modeling_qwen3_5 as qwen
    except (ImportError, OSError, RuntimeError) as error:
        LOG.warning("Ядра FLA недоступны: %s", error)
        return
    def project_patch(self, hidden_states):
        # Qwen's patch convolution covers exactly one input patch with no overlap.
        weights = self.proj.weight.flatten(1)
        patches = hidden_states.reshape(-1, weights.shape[1]).to(weights.dtype)
        return torch.nn.functional.linear(patches, weights, self.proj.bias)

    qwen.Qwen3_5VisionPatchEmbed.forward = project_patch
    for name, kernel in (
        ("torch_chunk_gated_delta_rule", chunk_gated_delta_rule),
        ("torch_recurrent_gated_delta_rule", fused_recurrent_gated_delta_rule),
    ):
        original = getattr(qwen, name)
        if getattr(original, "amadea_accelerated", False):
            continue

        def accelerated(*args, fast=kernel, fallback=original, operation=name, **kwargs):
            try:
                return fast(*args, **kwargs)
            except (OSError, RuntimeError, ValueError) as error:
                LOG.warning("Вычисление %s вернулось к PyTorch: %s", operation, error)
                setattr(qwen, operation, fallback)
                return fallback(*args, **kwargs)

        accelerated.amadea_accelerated = True
        setattr(qwen, name, accelerated)
