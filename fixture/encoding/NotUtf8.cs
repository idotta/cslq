namespace Fixture.Encoding;

// Deliberately not valid UTF-8. The Caption literal below holds the byte pair E9 A0 --
// CP1252 for an accented letter and a non-breaking space -- which no UTF-8 decoder can
// read. Nothing compiles this file: there is no .csproj in this directory, and Cslq.slnx
// does not reach it either. It exists for the readers. A `diag` walk names it and carries
// on; a command handed it as a target refuses it.
public class NotUtf8
{
    public string Caption => "café  latte";
}
