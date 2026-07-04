using HomeHarbor.Api.Services;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class SetupPairingServiceTests
{
    [TestMethod]
    public void IsValid_Returns_False_For_Null_Or_Blank_Code()
    {
        var pairings = CreateService();

        Assert.IsFalse(pairings.IsValid(null));
        Assert.IsFalse(pairings.IsValid(""));
        Assert.IsFalse(pairings.IsValid("   "));
    }

    [TestMethod]
    public void IsValid_Returns_True_For_Current_Ticket_Code()
    {
        var pairings = CreateService();
        var ticket = pairings.GetOrCreate("https://homeharbor.test");

        Assert.IsTrue(pairings.IsValid(ticket.Code));
    }

    [TestMethod]
    public void IsValid_Trims_And_Ignores_Code_Case()
    {
        var pairings = CreateService();
        var ticket = pairings.GetOrCreate("https://homeharbor.test");

        Assert.IsTrue(pairings.IsValid($"  {ticket.Code.ToLowerInvariant()}  "));
    }

    [TestMethod]
    public void Consume_Allows_Null_Or_Blank_Code_As_No_Op()
    {
        var pairings = CreateService();
        var ticket = pairings.GetOrCreate("https://homeharbor.test");

        pairings.Consume(null);
        pairings.Consume("");
        pairings.Consume("   ");

        Assert.IsTrue(pairings.IsValid(ticket.Code));
    }

    [TestMethod]
    public void Consume_Invalidates_Current_Ticket_Code()
    {
        var pairings = CreateService();
        var ticket = pairings.GetOrCreate("https://homeharbor.test");

        pairings.Consume(ticket.Code);

        Assert.IsFalse(pairings.IsValid(ticket.Code));
    }

    private static SetupPairingService CreateService()
        => new(new TokenGenerator());
}
