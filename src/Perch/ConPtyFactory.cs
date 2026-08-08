using System;

namespace Perch;

/// The Windows pty backend: CreatePseudoConsole via ConPty.
internal sealed class ConPtyFactory : IPtyFactory
{
    public IPty Start(string command, int cols, int rows, string? cwd) =>
        ConPty.Start(command, cols: cols, rows: rows, cwd: cwd);
}
