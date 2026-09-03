using System;
using System.Collections.Generic;
using System.Linq;

namespace Perch;

/// What a bot can look like, and how the host picks. The vocabulary is the
/// page's (src/web/src/bot-face.ts renders it); the two lists must agree, and
/// the page tolerates a value it doesn't know by falling back to a default,
/// so an older page never breaks on a newer team file.
///
/// The HAT is meaning: it says which position a bot holds, so it is chosen
/// from the position's name and every bot in that position wears it. The
/// rest is character, drawn at random when the bot is created and then kept,
/// so a bot looks the same on every machine and every day.
internal static class TeamLooks
{
    public static readonly string[] Hats = { "captain", "beanie", "hardhat", "beret", "deerstalker", "tophat" };
    public static readonly string[] Eyewear = { "monocle", "pincenez", "round", "rect", "loupe", "goggles", "spectacles" };
    public static readonly string[] Extras = { "none", "bowtie", "tie", "scarf", "crest", "headset", "pencil", "spanner", "magnifier" };
    public static readonly string[] Tempers = { "steady", "quick", "curious", "wary", "keen", "lead" };

    /// Words in a position's name that decide its hat, first match wins. The
    /// order matters where names overlap ("frontend lead" is a lead).
    private static readonly (string[] Words, string Hat)[] HatRules =
    {
        (new[] { "lead", "manager", "pm", "product", "owner", "captain", "director", "head" }, "captain"),
        (new[] { "design", "ux", "ui", "visual", "brand", "art" }, "beret"),
        (new[] { "qa", "test", "quality", "review", "audit", "inspector" }, "deerstalker"),
        (new[] { "analy", "research", "data", "finance", "senior", "architect", "advis" }, "tophat"),
        (new[] { "backend", "back-end", "back end", "infra", "devops", "server", "api", "database", "sql", "platform", "ops", "sre" }, "hardhat"),
        (new[] { "front", "web", "mobile", "app", "client" }, "beanie"),
    };

    /// The hat for a position name. No word matches → a hat picked by the
    /// name's hash, so two unrelated positions usually differ but the same
    /// name always gets the same hat.
    public static string HatFor(string positionName)
    {
        var name = (positionName ?? "").ToLowerInvariant();
        var words = name.Split(new[] { ' ', '-', '_', '/', ',', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var (keys, hat) in HatRules)
            foreach (var key in keys)
            {
                // Whole-word for the short keys ("pm", "qa", "ui", "api"),
                // prefix for the rest ("analy" covers analyst and analytics).
                var hit = key.Length <= 3
                    ? words.Contains(key)
                    : name.Contains(key, StringComparison.Ordinal);
                if (hit) return hat;
            }
        return Hats[StableHash(name) % Hats.Length];
    }

    /// A random look. The monocle is the mascot's own, so it comes up more
    /// often than any one alternative; "none" is likewise the most common
    /// extra, so a room isn't a costume party.
    public static TeamLook RandomLook(Random rng)
    {
        var eyewear = rng.Next(3) == 0 ? "monocle" : Eyewear[rng.Next(Eyewear.Length)];
        var extra = rng.Next(3) == 0 ? "none" : Extras[rng.Next(Extras.Length)];
        return new TeamLook
        {
            Eyewear = eyewear,
            Extra = extra,
            Temper = Tempers[rng.Next(Tempers.Length)],
        };
    }

    /// A look with any unknown value replaced by its default — for documents
    /// edited by hand or written by a newer Perch.
    public static TeamLook Normalize(TeamLook? look)
    {
        var l = look ?? new TeamLook();
        return new TeamLook
        {
            Eyewear = Eyewear.Contains(l.Eyewear) ? l.Eyewear : "monocle",
            Extra = Extras.Contains(l.Extra) ? l.Extra : "none",
            Temper = Tempers.Contains(l.Temper) ? l.Temper : "steady",
        };
    }

    public static string NormalizeHat(string? hat, string positionName)
        => hat != null && Hats.Contains(hat) ? hat : HatFor(positionName);

    /// FNV-1a over the string; deterministic across runs (string.GetHashCode
    /// is randomized per process).
    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return (int)(h & 0x7fffffff);
        }
    }
}
