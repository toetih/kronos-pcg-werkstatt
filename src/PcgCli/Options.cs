namespace PcgCli;

/// <summary>
/// Einfache Auswertung von "--name wert", "--schalter" und Positionsargumenten.
/// </summary>
public class Options
{
    private readonly Dictionary<string, string> _named = new();
    private readonly List<string> _positional = new();

    public static Options Parse(IEnumerable<string> args)
    {
        var options = new Options();
        var list = args.ToList();
        for (var index = 0; index < list.Count; index++)
        {
            if (list[index].StartsWith("--"))
            {
                var name = list[index][2..];
                var hasValue = index + 1 < list.Count && !list[index + 1].StartsWith("--");
                options._named[name] = hasValue ? list[++index] : string.Empty;
            }
            else
            {
                options._positional.Add(list[index]);
            }
        }

        return options;
    }

    public string Positional(int index) => index < _positional.Count ? _positional[index] : null;

    public string Get(string name) => _named.TryGetValue(name, out var value) ? value : null;

    public bool Has(string name) => _named.ContainsKey(name);

    public string Require(string name) => Get(name) ?? throw new ArgumentException($"--{name} fehlt.");
}
