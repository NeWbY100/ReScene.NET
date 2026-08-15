using System.Text;

namespace ReScene.Cli.Tests;

/// <summary>
/// Captures Console.Out and Console.Error for the lifetime of the instance, restoring the
/// originals on dispose. The originals are held inside a restore action rather than as
/// fields: they are borrowed process-globals this type must never dispose, and
/// disposable-typed fields would (rightly) trip CA2213's "dispose your fields" analysis.
/// Only safe because the assembly disables test parallelization (see TestCollectionConfig.cs)
/// — the console is process-global.
/// </summary>
internal sealed class ConsoleCapture : IDisposable
{
    private readonly Action _restore;
    private readonly StringWriter _out = new(new StringBuilder());
    private readonly StringWriter _error = new(new StringBuilder());

    public ConsoleCapture()
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        _restore = () =>
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        };

        Console.SetOut(_out);
        Console.SetError(_error);
    }

    public string Stdout => _out.ToString();

    public string Stderr => _error.ToString();

    public void Dispose()
    {
        _restore();
        _out.Dispose();
        _error.Dispose();
    }
}
