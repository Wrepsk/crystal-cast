using System.Text;

namespace CrystalCast.Twitch;

/// <summary>
/// One line from Twitch chat's IRC connection: <c>@tags :prefix COMMAND params :trailing</c>.
/// Only the parts the chat tab reads are kept.
/// </summary>
internal sealed record TwitchIrcMessage(
    IReadOnlyDictionary<string, string> Tags,
    string Prefix,
    string Command,
    IReadOnlyList<string> Parameters,
    string Trailing)
{
    public string Tag(string name) => Tags.TryGetValue(name, out var value) ? value : string.Empty;

    /// <summary>The login in <c>nick!user@host</c>, or empty.</summary>
    public string PrefixLogin
    {
        get
        {
            var bang = Prefix.IndexOf('!');
            return bang > 0 ? Prefix[..bang] : string.Empty;
        }
    }

    /// <summary>The channel the message is for, without the leading #, lowercased.</summary>
    public string Channel => Parameters.Count > 0 && Parameters[0].StartsWith('#')
        ? Parameters[0][1..].ToLowerInvariant()
        : string.Empty;

    public static TwitchIrcMessage? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var span = line.AsSpan().TrimEnd("\r\n");
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (span.StartsWith("@"))
        {
            var space = span.IndexOf(' ');
            if (space < 0)
                return null;

            foreach (var pair in span[1..space].ToString().Split(';'))
            {
                var equals = pair.IndexOf('=');
                if (equals < 0)
                    tags[pair] = string.Empty;
                else
                    tags[pair[..equals]] = UnescapeTagValue(pair[(equals + 1)..]);
            }

            span = span[(space + 1)..].TrimStart(' ');
        }

        var prefix = string.Empty;
        if (span.StartsWith(":"))
        {
            var space = span.IndexOf(' ');
            if (space < 0)
                return null;

            prefix = span[1..space].ToString();
            span = span[(space + 1)..].TrimStart(' ');
        }

        var trailing = string.Empty;
        var trailingStart = span.IndexOf(" :");
        if (trailingStart >= 0)
        {
            trailing = span[(trailingStart + 2)..].ToString();
            span = span[..trailingStart];
        }

        var words = span.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return null;

        return new TwitchIrcMessage(tags, prefix, words[0].ToUpperInvariant(), words[1..], trailing);
    }

    /// <summary>IRCv3 tag escapes: <c>\s</c> space, <c>\:</c> semicolon, <c>\\</c> backslash, <c>\r</c>, <c>\n</c>.</summary>
    internal static string UnescapeTagValue(string value)
    {
        if (!value.Contains('\\'))
            return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\\' || i == value.Length - 1)
            {
                if (c != '\\')
                    builder.Append(c);
                continue;
            }

            i++;
            builder.Append(value[i] switch
            {
                's' => ' ',
                ':' => ';',
                'r' => '\r',
                'n' => '\n',
                _ => value[i],
            });
        }

        return builder.ToString();
    }
}
