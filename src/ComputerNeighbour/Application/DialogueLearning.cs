using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComputerNeighbour.Configuration;
using ComputerNeighbour.ConsoleInterface;
using ComputerNeighbour.Inference;
using ComputerNeighbour.Platform;

namespace ComputerNeighbour.Application;

internal sealed record DialogueExample(string Prompt, string Reply);

// Commands and model reloads run in the console event loop. The worker only writes an unpublished adapter.
internal sealed class DialogueLearning(PortablePaths paths, AppConfig config, LocalModel model, ICompanionOutput ui) : IAsyncDisposable
{
    private const string DirectoryName = "data/lora";
    private const string AmadeusDirectory = DirectoryName + "/amadeus";
    private const string ExamplesName = DirectoryName + "/examples.json";
    private const string PythonName = "runtimes/lora/python/Scripts/python.exe";
    private const string ScriptName = "scripts/Train-Lora.py";
    private const string BaseSha256 = "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4";
    private const string AmadeusSha256 = "e633719c3230abfecccf461ecd061034cf2a13006ce7b181be93dbbec976a53c";
    private const int MaxExamples = 32;
    private TrainingRun? running;
    private string? regularAdapter;
    private bool preview;

    private string AdapterDirectory => config.Model.UseAbliterated ? AmadeusDirectory : DirectoryName;
    private string CandidateName => AdapterDirectory + "/candidate.gguf";
    private string ActiveName => AdapterDirectory + "/active.gguf";
    private string StagedName => AdapterDirectory + "/candidate.part";
    private string BaseName => config.Model.UseAbliterated ? "runtimes/lora/amadeus" : "runtimes/lora/base";

    internal bool IsTraining => running is not null;

    internal async Task StartModelAsync(CancellationToken ct)
    {
        if (config.Model.AutoStart)
        {
            try
            {
                var active = paths.Inside(ActiveName);
                if (File.Exists(active))
                {
                    await VerifyBaseAsync(ct);
                    await model.StartAsync(active, false, ct);
                    regularAdapter = active;
                    return;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { ui.Write("[LoRA] The saved adapter could not be loaded. Starting the base model. " + ex.Message); }
        }
        await model.StartAsync(ct);
    }

    internal async Task HandleAsync(string input, CompanionEngine engine, Func<CancellationToken, ValueTask> signal, CancellationToken ct)
    {
        var args = input.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        var argument = args.Length == 2 ? args[1].Trim() : "";
        if (engine.IsIncognito && command is not ("help" or "status" or "preview"))
        {
            ui.Write("[LoRA] Incognito can preview a ready adapter. Use the regular session to collect examples or train.");
            return;
        }
        switch (command)
        {
            case "good":
            case "teach":
                if (engine.LastDialogueExample is not { } example) throw new InvalidOperationException("Complete a regular text conversation turn first.");
                if (command == "teach" && argument.Length == 0) throw new ArgumentException("Usage: /lora teach YOUR_PREFERRED_REPLY");
                await SaveExampleAsync(command == "teach" ? example with { Reply = argument } : example, ct);
                break;
            case "examples":
                var examples = await ReadExamplesAsync(ct);
                ui.Write(examples.Count == 0 ? "[LoRA] No examples saved." : string.Join('\n', examples.Select((item, i) =>
                    $"{i + 1}. {Preview(item.Prompt)} → {Preview(item.Reply)}")));
                break;
            case "remove":
                var saved = await ReadExamplesAsync(ct);
                if (!int.TryParse(argument, out var number) || number < 1 || number > saved.Count)
                    throw new ArgumentException("Use /lora examples and /lora remove NUMBER.");
                saved.RemoveAt(number - 1);
                await WriteExamplesAsync(saved, ct);
                ui.Write("[LoRA] Example removed from future training runs.");
                break;
            case "train":
                await TrainAsync(signal, ct);
                break;
            case "cancel":
                if (running is null) ui.Write("[LoRA] No training is running.");
                else { running.Cancellation.Cancel(); ui.Write("[LoRA] Cancellation requested."); }
                break;
            case "apply":
                await ApplyAsync(ct);
                break;
            case "off":
                await DisableAsync(ct);
                ui.Write("[LoRA] Base model active.");
                break;
            case "preview":
                if (!engine.IsIncognito) throw new InvalidOperationException("Start incognito before previewing a LoRA adapter.");
                await model.ReloadAsync(await GetCandidateAsync(ct), false, ct);
                preview = true;
                ui.Write("[LoRA] The candidate is active in this incognito session. /stop incognito restores the regular adapter.");
                break;
            case "clear" when argument == "CONFIRM":
                await ClearAsync(ct);
                ui.Write("[LoRA] Examples and trained adapters cleared.");
                break;
            case "status":
                var count = (await ReadExamplesAsync(ct)).Count;
                ui.Write($"[LoRA] {count}/{MaxExamples} approved examples. Training: {(IsTraining ? "running" : "idle")}. " +
                    $"Candidate: {(File.Exists(paths.Inside(CandidateName)) ? "ready" : "none")}. Active: {(preview ? "incognito preview" : regularAdapter is null ? "base model" : "trained adapter")}.");
                break;
            default:
                ui.Write("""
                    /lora good              Approve the last ordinary dialogue reply
                    /lora teach REPLY       Save your preferred reply to that message
                    /lora examples          Inspect saved training examples
                    /lora remove NUMBER     Exclude an example from future training
                    /lora train             Train a candidate locally from 4–32 approved examples
                    /lora status, /lora cancel
                    /lora preview           Try the candidate in incognito
                    /lora apply, /lora off  Enable the candidate or return to the base model
                    /lora clear CONFIRM     Erase the training examples and adapters
                    """);
                break;
        }
    }

    private static string Preview(string text) => text[..Math.Min(110, text.Length)].ReplaceLineEndings(" ");

    private async Task<List<DialogueExample>> ReadExamplesAsync(CancellationToken ct)
    {
        var file = paths.Inside(ExamplesName);
        if (!File.Exists(file)) return [];
        await using var stream = File.OpenRead(file);
        var examples = await JsonSerializer.DeserializeAsync<List<DialogueExample>>(stream, AppConfig.Json, ct);
        if (examples is null || examples.Count > MaxExamples || examples.Any(item => item is null ||
            string.IsNullOrWhiteSpace(item.Prompt) || string.IsNullOrWhiteSpace(item.Reply)))
            throw new InvalidDataException("The saved LoRA example file is invalid.");
        return examples;
    }

    private async Task WriteExamplesAsync(List<DialogueExample> examples, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Inside(DirectoryName));
        var temporary = paths.Inside(ExamplesName + ".tmp");
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, examples, AppConfig.Json, ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, paths.Inside(ExamplesName), true);
        }
        finally { File.Delete(temporary); }
    }

    private async Task SaveExampleAsync(DialogueExample example, CancellationToken ct)
    {
        if (example.Prompt.Length > 1800 || example.Reply.Length > 1800)
            throw new ArgumentException("Use a shorter dialogue example (at most 1800 characters per message).");
        var examples = await ReadExamplesAsync(ct);
        var index = examples.FindIndex(item => item.Prompt == example.Prompt);
        if (index >= 0) examples[index] = example;
        else
        {
            if (examples.Count == MaxExamples) throw new InvalidOperationException("Remove an example before saving another.");
            examples.Add(example);
        }
        await WriteExamplesAsync(examples, ct);
        ui.Write($"[LoRA] Example saved ({examples.Count}/{MaxExamples}).");
    }

    private async Task VerifyBaseAsync(CancellationToken ct)
    {
        if (!config.Model.AutoStart) throw new InvalidOperationException("LoRA requires the app-managed llama-server (model.autoStart=true).");
        await using var weights = File.OpenRead(paths.Inside(config.Model.EffectiveModelFile));
        var (length, hash) = config.Model.UseAbliterated ? (2783446944L, AmadeusSha256) : (2740937888L, BaseSha256);
        if (weights.Length != length || !Convert.ToHexString(await SHA256.HashDataAsync(weights, ct)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("LoRA requires the verified Qwen3.5-4B Q4_K_M weights for the current model profile.");
    }

    private void RequireIdle()
    {
        if (IsTraining) throw new InvalidOperationException("Wait for LoRA training or use /lora cancel.");
    }

    private async Task<string> GetCandidateAsync(CancellationToken ct)
    {
        RequireIdle();
        var candidate = paths.Inside(CandidateName);
        if (!File.Exists(candidate)) throw new FileNotFoundException("Train a LoRA candidate first with /lora train.");
        await VerifyBaseAsync(ct);
        return candidate;
    }

    private async Task ApplyAsync(CancellationToken ct)
    {
        var candidate = await GetCandidateAsync(ct);
        var active = paths.Inside(ActiveName);
        var temporary = paths.Inside(ActiveName + ".tmp");
        await model.ReloadAsync(candidate, false, ct);
        try
        {
            File.Copy(candidate, temporary, true);
            File.Move(temporary, active, true);
            regularAdapter = active;
        }
        catch
        {
            await model.ReloadAsync(regularAdapter, false, ct);
            throw;
        }
        finally { File.Delete(temporary); }
        ui.Write("[LoRA] Candidate enabled for your ordinary dialogue. /lora off returns to the base model.");
    }

    private async Task DisableAsync(CancellationToken ct, bool restoreDevice = false)
    {
        RequireIdle();
        if (restoreDevice || model.LoadedAdapter is not null) await model.ReloadAsync(null, false, ct);
        var active = paths.Inside(ActiveName);
        try { if (File.Exists(active)) File.Delete(active); }
        catch
        {
            if (regularAdapter is not null) await model.ReloadAsync(regularAdapter, false, ct);
            throw;
        }
        regularAdapter = null;
    }

    internal async Task EndPreviewAsync(CancellationToken ct)
    {
        if (!preview) return;
        await model.ReloadAsync(regularAdapter, false, ct);
        preview = false;
    }

    private async Task TrainAsync(Func<CancellationToken, ValueTask> signal, CancellationToken ct)
    {
        RequireIdle();
        var examples = await ReadExamplesAsync(ct);
        if (examples.Count < 4) throw new InvalidOperationException("Save at least four different exchanges using /lora good or /lora teach.");
        var shards = config.Model.UseAbliterated
            ? new[] { "model-00001-of-00002.safetensors", "model-00002-of-00002.safetensors", "model-mtp.safetensors" }
            : new[] { "model.safetensors-00001-of-00002.safetensors", "model.safetensors-00002-of-00002.safetensors" };
        var baseFiles = new[] { "config.json", "tokenizer.json", "tokenizer_config.json", "chat_template.jinja", "model.safetensors.index.json" }.Concat(shards);
        foreach (var name in baseFiles.Select(file => BaseName + "/" + file).Concat([PythonName, ScriptName]))
            if (!File.Exists(paths.Inside(name))) throw new FileNotFoundException("LoRA dependencies are missing. Run Setup.cmd -Only LoRA" +
                (config.Model.UseAbliterated ? " -Abliterated." : "."));
        await VerifyBaseAsync(ct);
        Directory.CreateDirectory(paths.Inside(AdapterDirectory));
        var staged = paths.Inside(StagedName);
        File.Delete(staged);
        ui.Write("[LoRA] Moving dialogue to the CPU while the GPU trains. Replies may be slower.");
        await model.ReloadAsync(regularAdapter, true, ct);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(TimeSpan.FromMinutes(20));
        var work = RunTrainerAsync(examples, staged, cancellation.Token);
        running = new(cancellation, work);
        _ = SignalWhenDoneAsync(work, signal, ct);
    }

    private async Task RunTrainerAsync(List<DialogueExample> examples, string staged, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(paths.Inside(PythonName))
        {
            WorkingDirectory = paths.Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-B", "-u", paths.Inside(ScriptName), "--base", paths.Inside(BaseName), "--output", staged })
            start.ArgumentList.Add(arg);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.Environment["HF_HUB_DISABLE_PROGRESS_BARS"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Could not start the LoRA trainer.");
        ChildProcessJob.Attach(process);
        var output = ReportProgressAsync(process.StandardOutput);
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var request = JsonSerializer.Serialize(new { character = config.CharacterName, examples }, AppConfig.Json);
            await process.StandardInput.WriteAsync(request.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            await output;
            var stderr = await errors;
            if (process.ExitCode != 0) throw new IOException(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim()
                ?? $"Trainer exited with code {process.ExitCode}.");
            using var adapter = File.OpenRead(staged);
            var header = new byte[4];
            adapter.ReadExactly(header);
            if (!header.AsSpan().SequenceEqual("GGUF"u8)) throw new InvalidDataException("The trainer did not export a GGUF adapter.");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            await process.WaitForExitAsync();
            try { await Task.WhenAll(output, errors); } catch { /* Keep the original trainer failure. */ }
            throw;
        }
    }

    private async Task ReportProgressAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line) ui.Write("[LoRA] " + line);
    }

    private static async Task SignalWhenDoneAsync(Task work, Func<CancellationToken, ValueTask> signal, CancellationToken ct)
    {
        try { await work; } catch { /* The event loop reports the result. */ }
        try { await signal(ct); } catch (OperationCanceledException) { }
    }

    internal async Task FinishTrainingAsync(CancellationToken ct)
    {
        if (running is not { Work.IsCompleted: true } run) return;
        try
        {
            await run.Work;
            run.Cancellation.Token.ThrowIfCancellationRequested();
            File.Move(paths.Inside(StagedName), paths.Inside(CandidateName), true);
            ui.Write("[LoRA] Candidate ready. Try it in incognito with /lora preview or enable it with /lora apply.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { ui.Write("[LoRA] Training cancelled or timed out."); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { ui.Write("[LoRA] Training failed. " + ex.Message); }
        finally
        {
            run.Cancellation.Dispose();
            running = null;
            DeleteStaged();
        }
        await model.ReloadAsync(regularAdapter, false, ct);
    }

    private void DeleteStaged()
    {
        try { File.Delete(paths.Inside(StagedName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { ui.Write("[LoRA] Could not remove an unfinished adapter. " + ex.Message); }
    }

    internal async Task ClearAsync(CancellationToken ct)
    {
        if (!Directory.Exists(paths.Inside(DirectoryName))) return;
        var wasTraining = IsTraining;
        await DisposeAsync();
        await DisableAsync(ct, wasTraining);
        File.Delete(paths.Inside(ExamplesName));
        File.Delete(paths.Inside(ExamplesName + ".tmp"));
        foreach (var directory in new[] { DirectoryName, AmadeusDirectory })
        {
            if (!Directory.Exists(paths.Inside(directory))) continue;
            foreach (var file in new[] { "active.gguf", "active.gguf.tmp", "candidate.gguf", "candidate.part" })
                File.Delete(paths.Inside(directory + "/" + file));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (running is not { } run) return;
        run.Cancellation.Cancel();
        try { await run.Work; } catch { /* A cancelled run is never published. */ }
        run.Cancellation.Dispose();
        running = null;
        DeleteStaged();
    }

    private sealed record TrainingRun(CancellationTokenSource Cancellation, Task Work);
}
