using BeneficialStrategies.StripeCrmBridge.HubSpot;

namespace BeneficialStrategies.StripeCrmBridge.UnitTest.HubSpot;

/// <summary>How a display name becomes First Name and Last Name, and when the sync may write them (#65).</summary>
public class ContactNamesTests
{
    [Theory]
    [InlineData("Nancy Ward", "Nancy", "Ward")]
    [InlineData("Joe Somebody", "Joe", "Somebody")]
    [InlineData("Mary Ann Smith", "Mary Ann", "Smith")]
    [InlineData("  Joe   Somebody  ", "Joe", "Somebody")]
    [InlineData("Cher", "Cher", null)]
    public void A_display_name_is_split_at_its_last_word(string display, string first, string? last) =>
        Assert.Equal((first, last), ContactNames.Split(display));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_split_gives_nothing_to_write(string? display) =>
        Assert.Null(ContactNames.Split(display));

    [Fact]
    public void A_contact_that_does_not_exist_yet_gets_the_names() =>
        Assert.True(ContactNames.MayWrite(hubSpotAnswered: true, contactExists: false, null, null));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" ", null)]
    public void A_contact_with_both_names_empty_gets_the_names(string? first, string? last) =>
        Assert.True(ContactNames.MayWrite(true, true, first, last));

    [Theory]
    [InlineData("Nancy", "Ward")]
    [InlineData("Nancy", null)]
    [InlineData(null, "Ward")]
    public void A_contact_with_either_name_set_keeps_its_names(string? first, string? last) =>
        Assert.False(ContactNames.MayWrite(true, true, first, last));

    [Fact]
    public void When_HubSpot_could_not_be_asked_the_names_are_left_alone() =>
        Assert.False(ContactNames.MayWrite(hubSpotAnswered: false, contactExists: false, null, null));
}
