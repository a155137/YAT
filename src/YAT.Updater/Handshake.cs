using System.IO.Pipes;
using YAT.Application.Updates;

namespace YAT.Updater;

// YAT's answer to "ready".
internal enum HandshakeAnswer
{
    // YAT is closing: install once it has exited.
    Go,

    // YAT is not closing (the user cancelled): change nothing.
    Cancel,

    // The pipe closed, or said something else: YAT went away without asking. Change nothing.
    Gone
}

// The updater's side of UpdateInstallProtocol's pipes: one line out ("ready", or "error <reason>"), one line in.
internal sealed class Handshake : IDisposable
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly IDisposable[] _owned;

    public Handshake(TextReader input, TextWriter output, params IDisposable[] owned)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _input = input;
        _output = output;
        _owned = owned;
    }

    // Over the anonymous pipes whose handles YAT passed.
    public static Handshake Open(string pipeIn, string pipeOut)
    {
        var input = new AnonymousPipeClientStream(PipeDirection.In, pipeIn);
        try
        {
            var output = new AnonymousPipeClientStream(PipeDirection.Out, pipeOut);
            return new Handshake(new StreamReader(input), new StreamWriter(output) { AutoFlush = true }, input, output);
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    public void Ready() => Send(UpdateInstallProtocol.Ready);

    public void Refuse(string reason) => Send(UpdateInstallProtocol.ErrorPrefix + reason.ReplaceLineEndings(" "));

    // Blocks until YAT answers or goes away.
    public HandshakeAnswer WaitForAnswer()
    {
        string? line;
        try
        {
            line = _input.ReadLine();
        }
        catch (IOException)
        {
            return HandshakeAnswer.Gone;
        }

        return line switch
        {
            UpdateInstallProtocol.Go => HandshakeAnswer.Go,
            UpdateInstallProtocol.Cancel => HandshakeAnswer.Cancel,
            _ => HandshakeAnswer.Gone
        };
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }

    private void Send(string line)
    {
        try
        {
            _output.WriteLine(line);
            _output.Flush();
        }
        catch (IOException)
        {
            // YAT is gone; WaitForAnswer says so.
        }
    }
}
