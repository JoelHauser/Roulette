namespace Roulette.Game;

/// <summary>
/// Table rules.
///
/// Chips, not currency. The engine takes an int and returns an int and has no idea
/// what a rouble is -- what a chip is worth belongs with the wallet. Keeping that
/// boundary is what makes the rules testable, and it is the same boundary Poker and
/// Blackjack draw.
/// </summary>
public sealed record RouletteRules
{
    /// <summary>
    /// Single zero by default. A European wheel is a 2.70% house edge against an
    /// American wheel's 5.26%, and there is no reason to give a single player the
    /// worse of the two.
    /// </summary>
    public WheelKind Wheel { get; init; } = WheelKind.European;

    /// <summary>
    /// The smallest chip, and therefore the smallest bet and the step every bet
    /// moves in. A stake that cannot be built out of chips is one the table can
    /// never show honestly -- the lesson Poker learned by drawing a 30,000 pot as a
    /// 25k chip with 5,000 stranded.
    /// </summary>
    public int MinBet { get; init; } = 10_000;

    /// <summary>
    /// The most that may ride on a single straight-up number.
    ///
    /// **This is the house's only protection and it is not optional.** At 35 to 1 a
    /// single number is the whole bankroll in one bet: without a cap, a player can
    /// stake everything on 17 and the mod is a coin-flip for the stash rather than a
    /// game with an edge. Capped per bet rather than per spin, because thirty-six
    /// straight-up bets at the cap is simply the same money spread out and costs the
    /// house the same edge.
    /// </summary>
    public int MaxStraight { get; init; } = 500_000;

    /// <summary>
    /// The most that may ride on one even-money bet. Higher than the straight cap
    /// because it pays 1 to 1: the exposure is the stake, not thirty-five times it.
    /// </summary>
    public int MaxEvenMoney { get; init; } = 5_000_000;

    /// <summary>The most that may be on the cloth in total when the wheel turns.</summary>
    public int MaxTotalStake { get; init; } = 10_000_000;

    /// <summary>How many bets may be on the cloth at once.</summary>
    public int MaxBets { get; init; } = 20;

    /// <summary>
    /// The ceiling for a given bet, which is the straight cap scaled down by what it
    /// pays. A bet paying 35 to 1 and one paying 1 to 1 risk the house very
    /// different amounts for the same stake, so one flat cap would either strangle
    /// the even-money bets or leave the straight ones wide open.
    /// </summary>
    public int MaxFor(BetKind kind)
    {
        var odds = Payouts.ToOne(kind);

        return odds <= 1
            ? MaxEvenMoney
            : Math.Max(MinBet, MaxEvenMoney / odds);
    }
}
