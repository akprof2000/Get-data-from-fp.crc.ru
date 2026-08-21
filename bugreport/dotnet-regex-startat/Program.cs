using System.Text;
using System.Text.RegularExpressions;

// Minimal repro for: Regex.Match(input, startat) returns a match BEFORE startat
// when RegexOptions.Compiled is used.
//
// Run:  dotnet run -f net10.0     (also -f net9.0, -f net8.0)

Console.OutputEncoding = Encoding.UTF8;

var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "input.txt"));

const string Pattern =
    @"наименование\s+(?:ПРТО|РЭС)\s+и\s+место\s+расположения\s*(?:\(адрес\))?[:\s]*" +
    @"(?:[^\n]+\n){0,3}?([^\n]+(?:Республика|область)[^\n]*)";

Console.WriteLine($".NET {Environment.Version}, input length = {text.Length}");
Console.WriteLine();

Check("Compiled", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
Check("interpreted", RegexOptions.IgnoreCase | RegexOptions.Singleline);
Check("NonBacktracking", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking);

static void Check(string label, RegexOptions options)
{
    var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "input.txt"));
    var rx = new Regex(Pattern, options, TimeSpan.FromSeconds(10));

    var first = rx.Match(text);
    if (!first.Success)
    {
        Console.WriteLine($"[{label}] no match at all");
        return;
    }

    int startat = first.Index + Math.Max(first.Length, 1);
    var second = rx.Match(text, startat);

    Console.WriteLine($"[{label}] first match: index={first.Index} length={first.Length}");
    Console.WriteLine($"[{label}] Match(text, {startat}) -> " +
        (second.Success ? $"index={second.Index} length={second.Length}" : "no match"));

    if (second.Success && second.Index < startat)
        Console.WriteLine($"[{label}] *** BUG: returned a match at {second.Index}, before startat={startat}");

    // Consequence: iteration never advances.
    int n = 0;
    foreach (Match _ in rx.Matches(text))
        if (++n >= 100_000) { Console.WriteLine($"[{label}] *** foreach over Matches() never terminates ({n} iterations, same match)"); break; }
    if (n < 100_000) Console.WriteLine($"[{label}] foreach over Matches(): {n} match(es) — terminates normally");

    // Same for NextMatch().
    var m = rx.Match(text);
    int k = 0;
    while (m.Success && ++k < 100_000) m = m.NextMatch();
    if (k >= 99_999) Console.WriteLine($"[{label}] *** NextMatch() never terminates either");

    Console.WriteLine();
}
