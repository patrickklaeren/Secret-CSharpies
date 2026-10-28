using System.Security.Cryptography;
using SecretSanta.Data;

namespace SecretSanta.Services;

public sealed record Pairing(string SenderId, string RecipientId);

public sealed record DrawResult(IReadOnlyList<Pairing> Pairs, IReadOnlyList<string> Problems)
{
    public bool Complete => Problems.Count == 0;
}

public static class Matcher
{
    public static bool Compatible(Participant sender, Participant recipient) =>
        sender.DiscordId != recipient.DiscordId && sender.Send && recipient.Receive &&
        !(sender.ShippingPreference == ShippingPreference.DigitalOnly && recipient.GiftPreference == GiftPreference.Physical);

    // Minimum-cost maximum flow: maximize matched people, then minimize overseas domestic-preference assignments.
    public static DrawResult Draw(IReadOnlyList<Participant> participants)
    {
        var senders = participants.Where(p => p.Send).ToArray();
        var recipients = participants.Where(p => p.Receive).ToArray();
        RandomNumberGenerator.Shuffle(senders.AsSpan());
        RandomNumberGenerator.Shuffle(recipients.AsSpan());
        var sink = 1 + senders.Length + recipients.Length;
        var graph = Enumerable.Range(0, sink + 1).Select(_ => new List<Edge>()).ToArray();

        void Add(int from, int to, int cost)
        {
            graph[from].Add(new Edge(to, graph[to].Count, 1, cost));
            graph[to].Add(new Edge(from, graph[from].Count - 1, 0, -cost));
        }

        for (var i = 0; i < senders.Length; i++)
        {
            Add(0, i + 1, 0);
            for (var j = 0; j < recipients.Length; j++)
                if (Compatible(senders[i], recipients[j]))
                    Add(i + 1, 1 + senders.Length + j,
                        senders[i].ShippingPreference == ShippingPreference.PreferDomestic &&
                        recipients[j].GiftPreference != GiftPreference.Digital && senders[i].Country != recipients[j].Country
                            ? 1
                            : 0);
        }

        for (var j = 0; j < recipients.Length; j++)
            Add(1 + senders.Length + j, sink, 0);

        while (true)
        {
            var distance = Enumerable.Repeat(int.MaxValue, graph.Length).ToArray();
            var previous = new (int Node, int Edge)[graph.Length];
            distance[0] = 0;

            for (var pass = 0; pass < graph.Length - 1; pass++)
            {
                var changed = false;

                for (var node = 0; node < graph.Length; node++)
                {
                    if (distance[node] == int.MaxValue)
                        continue;

                    for (var k = 0; k < graph[node].Count; k++)
                    {
                        var edge = graph[node][k];

                        if (edge.Capacity == 0 || distance[edge.To] <= distance[node] + edge.Cost)
                            continue;

                        distance[edge.To] = distance[node] + edge.Cost;
                        previous[edge.To] = (node, k);
                        changed = true;
                    }
                }

                if (!changed)
                    break;
            }

            if (distance[sink] == int.MaxValue)
                break;

            for (var node = sink; node != 0;)
            {
                var (parent, index) = previous[node];
                var edge = graph[parent][index];
                edge.Capacity--;
                graph[node][edge.Reverse].Capacity++;
                node = parent;
            }
        }

        var pairs = new List<Pairing>();
        for (var i = 0; i < senders.Length; i++)
            foreach (var edge in graph[i + 1].Where(e => e.To > senders.Length && e.To < sink && e.Capacity == 0))
                pairs.Add(new Pairing(senders[i].DiscordId, recipients[edge.To - senders.Length - 1].DiscordId));

        var problems = new List<string>();
        if (senders.Length == 0 && recipients.Length == 0)
            problems.Add("No submitted participants are available to draw.");

        foreach (var p in senders.Where(s => pairs.All(m => m.SenderId != s.DiscordId)))
            problems.Add($"@{p.Username} has no recipient: " + (recipients.Any(r => Compatible(p, r))
                ? "compatible recipients are needed by other senders. Add receivers or adjust participation/preferences."
                : "no eligible recipient fits their gift preference (self-matches are excluded)."));

        foreach (var p in recipients.Where(r => pairs.All(m => m.RecipientId != r.DiscordId)))
            problems.Add($"@{p.Username} has no Santa: " + (senders.Any(s => Compatible(s, p))
                ? "compatible senders are needed by other recipients. Add senders or adjust participation/preferences."
                : "no eligible sender fits their gift preference (self-matches are excluded)."));

        return new DrawResult(pairs, problems);
    }

    private sealed class Edge(int to, int reverse, int capacity, int cost)
    {
        public int To { get; } = to;
        public int Reverse { get; } = reverse;
        public int Capacity { get; set; } = capacity;
        public int Cost { get; } = cost;
    }
}
