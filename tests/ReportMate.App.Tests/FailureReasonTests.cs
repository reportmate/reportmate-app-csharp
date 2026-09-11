using ReportMate.App.Views.Fleet;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// The failures endpoint reports a reason code; the page shows a sentence. The
/// mapping is a plain lookup, so what is worth pinning is the fall-through: a code
/// the API grows before this table does must still reach the screen as itself.
/// </summary>
public class FailureReasonTests
{
    [Theory]
    [InlineData("invalid_passphrase", "Wrong passphrase")]
    [InlineData("missing_credentials", "No credentials")]
    [InlineData("serial_equals_hostname", "Serial matches hostname")]
    [InlineData("usage_write_failed", "Usage rows lost")]
    public void KnownCodeBecomesItsSentence(string code, string expected) =>
        Assert.Equal(expected, FailureReason.Label(code));

    /// <summary>
    /// Not "Unknown". A code nobody has labelled is still the real answer, and showing
    /// it raw is what makes the missing label visible instead of hiding a whole class
    /// of rejection behind one word.
    /// </summary>
    [Fact]
    public void UnlabelledCodeFallsThroughAsItself() =>
        Assert.Equal("brand_new_reason", FailureReason.Label("brand_new_reason"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AbsentCodeStaysEmpty(string? code) =>
        Assert.Equal("", FailureReason.Label(code));

    /// <summary>
    /// Every reason code the API's own docstring lists must have a sentence. This is
    /// the list that grows, and a code added upstream without one here shows a bare
    /// identifier in a column headed "Reason".
    /// </summary>
    [Theory]
    [InlineData("invalid_api_key")]
    [InlineData("invalid_bearer_token")]
    [InlineData("invalid_internal_secret")]
    [InlineData("insufficient_scope")]
    [InlineData("upload_aborted")]
    [InlineData("body_unreadable")]
    [InlineData("empty_body")]
    [InlineData("malformed_json")]
    [InlineData("invalid_payload")]
    [InlineData("empty_serial")]
    [InlineData("sentinel_serial")]
    [InlineData("short_serial")]
    [InlineData("hostname_serial")]
    [InlineData("nul_in_payload")]
    [InlineData("usage_out_of_bounds")]
    [InlineData("rate_limited")]
    [InlineData("internal_error")]
    public void DocumentedCodeIsLabelled(string code) =>
        Assert.NotEqual(code, FailureReason.Label(code));
}
