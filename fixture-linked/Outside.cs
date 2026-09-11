namespace Fixture.Core;

// Compiled by fixture/Core through a Compile Include that points at ../../fixture-linked, and
// living outside `--root fixture` -- the one shape the output rules had no label for. `sym
// Outside` used to print this file's machine-absolute path at exit 0, because
// `PathUri.Relative` falls back to the absolute path the moment the relative one starts with
// `..`. It renders under the `<external>/` label now, beside `<generated>/` and `<metadata>/`.
//
// It sits outside `fixture/` because that is the whole point, and outside `Cslq.slnx` because
// nothing in this repository may compile it: the root solution excludes `fixture/`, and this
// directory holds no project of its own.
public static class OutsideLinked
{
    public static int Answer() => 42;
}
