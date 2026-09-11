// dotnet new console's default shape: top-level statements and no type declaration anywhere,
// so sentinel inference has nothing to name and says so before the server starts.
Console.WriteLine(Greet("world"));

static string Greet(string name) => $"Hello, {name}";
