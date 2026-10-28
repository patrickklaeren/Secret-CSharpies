using SecretSanta.Data;
using SecretSanta.Services;
using Xunit;

namespace SecretSanta.Tests;

public class MatcherTests
{
    private static Participant P(string id, string country = "US") => new() { DiscordId = id, Username = id, Country = country, Send = true, Receive = true };

    [Fact]
    public void EveryoneGetsExactlyOneDifferentPerson()
    {
        var people = Enumerable.Range(0, 30).Select(i => P(i.ToString())).ToArray();
        var result = Matcher.Draw(people);
        Assert.True(result.Complete);
        Assert.Equal(30, result.Pairs.Select(p => p.SenderId).Distinct().Count());
        Assert.Equal(30, result.Pairs.Select(p => p.RecipientId).Distinct().Count());
        Assert.All(result.Pairs, p => Assert.NotEqual(p.SenderId, p.RecipientId));
    }

    [Fact]
    public void FindsMaximumPartialDrawAndReportsBothSides()
    {
        var a = P("a");
        a.ShippingPreference = ShippingPreference.DigitalOnly;
        var b = P("b");
        b.GiftPreference = GiftPreference.Physical;
        var result = Matcher.Draw([a, b]);
        Assert.False(result.Complete);
        Assert.Equal(new Pairing("b", "a"), Assert.Single(result.Pairs));
        Assert.Equal(2, result.Problems.Count);
    }

    [Fact]
    public void SupportsGiveOnlyAndReceiveOnly()
    {
        var giver = P("giver");
        giver.Receive = false;
        var receiver = P("receiver");
        receiver.Send = false;
        var result = Matcher.Draw([giver, receiver]);
        Assert.True(result.Complete);
        Assert.Equal(new Pairing("giver", "receiver"), Assert.Single(result.Pairs));
    }

    [Fact]
    public void DomesticPreferenceIsOptimizedButNotAHardConstraint()
    {
        var people = new[] { P("a"), P("b"), P("c", "GB"), P("d", "GB") };
        foreach (var p in people)
            p.ShippingPreference = ShippingPreference.PreferDomestic;

        var result = Matcher.Draw(people);
        Assert.True(result.Complete);
        Assert.All(result.Pairs, pair => Assert.Equal(people.Single(p => p.DiscordId == pair.SenderId).Country, people.Single(p => p.DiscordId == pair.RecipientId).Country));
        Assert.True(Matcher.Draw([people[0], people[2]]).Complete);
    }

    [Fact]
    public void EmptyAndSoloDrawsAreNotComplete()
    {
        Assert.False(Matcher.Draw([]).Complete);
        Assert.False(Matcher.Draw([P("only")]).Complete);
    }

    [Fact]
    public void MatchesExhaustiveOptimumForSmallMixedGroups()
    {
        var random = new Random(218);

        for (var sample = 0; sample < 100; sample++)
        {
            var people = Enumerable.Range(0, 5).Select(i => P(i.ToString(), random.Next(2) == 0 ? "US" : "GB")).ToArray();

            foreach (var p in people)
            {
                p.Send = random.Next(4) != 0;
                p.Receive = random.Next(4) != 0;
                p.GiftPreference = (GiftPreference)random.Next(3);
                p.ShippingPreference = (ShippingPreference)random.Next(4);
            }

            var senders = people.Where(p => p.Send).ToArray();
            var receivers = people.Where(p => p.Receive).ToArray();
            var bestCount = -1;
            var bestCost = int.MaxValue;
            int Cost(Participant s, Participant r) => s.ShippingPreference == ShippingPreference.PreferDomestic && r.GiftPreference != GiftPreference.Digital && s.Country != r.Country ? 1 : 0;

            void Enumerate(int i, HashSet<string> used, int count, int cost)
            {
                if (i == senders.Length)
                {
                    if (count > bestCount || (count == bestCount && cost < bestCost))
                    {
                        bestCount = count;
                        bestCost = cost;
                    }

                    return;
                }

                Enumerate(i + 1, used, count, cost);

                foreach (var r in receivers.Where(r => !used.Contains(r.DiscordId) && Matcher.Compatible(senders[i], r)))
                {
                    used.Add(r.DiscordId);
                    Enumerate(i + 1, used, count + 1, cost + Cost(senders[i], r));
                    used.Remove(r.DiscordId);
                }
            }

            Enumerate(0, [], 0, 0);
            var result = Matcher.Draw(people);
            Assert.Equal(bestCount, result.Pairs.Count);
            Assert.Equal(bestCost, result.Pairs.Sum(p => Cost(people.Single(s => s.DiscordId == p.SenderId), people.Single(r => r.DiscordId == p.RecipientId))));
        }
    }
}
