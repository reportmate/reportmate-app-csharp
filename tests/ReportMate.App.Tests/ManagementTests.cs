using System.Text.Json;
using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// The bootstrap-method ladder, ported from the web management report. These cases
/// exist because the raw enrollmentType field cannot answer the question: the two
/// clients use different vocabularies for it and the Windows answer is in a
/// different field entirely.
/// </summary>
public class ManagementTests
{
    private static string? Method(string json) =>
        Management.BootstrapMethod(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void AutopilotWins_OverTheEnrollmentType()
    {
        // The case the raw chart got wrong: 373 Windows devices are Autopilot
        // provisioned and every one of them reports enrollmentType "MDM Enrolled".
        Assert.Equal("Automated", Method("""
            {"autopilotConfig":{"activated":true},"enrollmentType":"MDM Enrolled",
             "enrollmentStatus":"Enrolled"}
            """));
    }

    [Fact]
    public void AutopilotActivatedAsAString_CountsToo() =>
        Assert.Equal("Automated", Method("""{"autopilotConfig":{"activated":"true"}}"""));

    [Fact]
    public void AutopilotPresentButNotActivated_FallsThroughToTheType() =>
        Assert.Equal("Manual", Method("""
            {"autopilotConfig":{"activated":false},"enrollmentType":"MDM Enrolled"}
            """));

    [Fact]
    public void MacAutomatedDeviceEnrollment_IsAutomated() =>
        Assert.Equal("Automated", Method("""{"enrollmentType":"Automated Device Enrollment"}"""));

    [Fact]
    public void UserApproved_IsItsOwnAnswer() =>
        Assert.Equal("User Approved", Method("""{"enrollmentType":"User Approved Enrollment"}"""));

    [Fact]
    public void WindowsWithoutAutopilot_IsManual() =>
        Assert.Equal("Manual", Method("""
            {"enrollmentType":"MDM Enrolled","enrollmentStatus":"Enrolled"}
            """));

    [Fact]
    public void AnEnrolledDeviceWithSomeOtherMethod_IsManual() =>
        Assert.Equal("Manual", Method("""
            {"enrollmentType":"Device Enrollment","enrollmentStatus":"Enrolled"}
            """));

    [Fact]
    public void ANotEnrolledDevice_SaysSo_RatherThanUnknown() =>
        // It has no bootstrap method, which is an answer; the Unknown bucket is for
        // rows we cannot account for.
        Assert.Equal("Not Enrolled", Method("""
            {"enrollmentType":"N/A","enrollmentStatus":"Not Enrolled"}
            """));

    [Theory]
    [InlineData("""{"enrollmentType":"N/A","enrollmentStatus":"Enrolled"}""")]
    [InlineData("""{"enrollmentStatus":"Enrolled"}""")]
    [InlineData("{}")]
    public void NothingAnswersTheQuestion_IsNull(string json)
    {
        // Null rather than a guess: the caller buckets these as Unknown, so they
        // stay in the population instead of quietly leaving it and inflating the
        // percentages of every other answer.
        Assert.Null(Method(json));
    }
}
