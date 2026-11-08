using System.Text;

namespace ComputerNeighbour.ConsoleInterface;

public interface ICompanionOutput { void Write(string text); }
public sealed class ConsoleUi : ICompanionOutput
{
    private readonly object gate=new();
    private readonly StringBuilder input=new();
    private bool reading;
    private bool incognito;
    private string Prompt=>incognito?"[Incognito] > ":"> ";

    internal void SetIncognito(bool active)
    {
        lock(gate)
        {
            if(incognito==active)return;
            incognito=active;
            if(reading&&!Console.IsOutputRedirected)
            {
                ClearInput();
                Console.Write(Prompt+input);
            }
        }
    }
    public void Write(string text)
    {
        lock(gate)
        {
            if(reading && !Console.IsOutputRedirected) ClearInput();
            Console.WriteLine(Sanitize(text));
            if(reading && !Console.IsOutputRedirected) Console.Write(Prompt+input);
        }
    }
    public static string Sanitize(string text)=>new(text.Where(c=>c is '\n' or '\t' || !char.IsControl(c)).ToArray());
    private static void ClearInput()
    {
        try { Console.Write('\r'); Console.Write(new string(' ',Math.Max(1,Console.WindowWidth-1))); Console.Write('\r'); }
        catch(IOException) { }
    }
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        if(Console.IsInputRedirected) return await Console.In.ReadLineAsync(ct);
        lock(gate) { reading=true;input.Clear();Console.Write(Prompt); }
        try
        {
            while(true)
            {
                ct.ThrowIfCancellationRequested();
                if(!Console.KeyAvailable) { await Task.Delay(25,ct);continue; }
                var key=Console.ReadKey(intercept:true);
                lock(gate)
                {
                    if(key.Key==ConsoleKey.Enter) { Console.WriteLine();return input.ToString(); }
                    if(key.Key==ConsoleKey.Backspace) { if(input.Length>0) { input.Length--;Console.Write("\b \b"); } }
                    else if(!char.IsControl(key.KeyChar) && key.KeyChar!='\0' && input.Length<12000) { input.Append(key.KeyChar);Console.Write(key.KeyChar); }
                }
            }
        }
        finally { lock(gate) reading=false; }
    }
}
