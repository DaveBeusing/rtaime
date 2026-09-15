using System.Globalization;
using rtaime.Core;

namespace rtaime.Tests.Unit;

public sealed class CoreDomainSemanticsTests
{
    [Fact]
    public void Identity_has_value_equality_and_canonical_round_trip_format()
    {
        var guid = Guid.Parse("2e48c465-a199-40b8-ae19-6fdba47390d8");
        var left = new Identity(guid);
        var right = Identity.Parse("2e48c465-a199-40b8-ae19-6fdba47390d8");

        Assert.Equal(left, right);
        Assert.Equal("2e48c465-a199-40b8-ae19-6fdba47390d8", left.ToString());
        Assert.False(left.IsEmpty);
    }

    [Fact]
    public void Identity_rejects_empty_or_non_canonical_values()
    {
        Assert.Throws<ArgumentException>(() => new Identity(Guid.Empty));
        Assert.False(Identity.TryParse("{2e48c465-a199-40b8-ae19-6fdba47390d8}", out _));
        Assert.False(Identity.TryParse(Guid.Empty.ToString("D"), out _));
    }

    [Fact]
    public void Revision_and_generation_advance_monotonically()
    {
        Assert.Equal(new Revision(1), Revision.Initial.Next());
        Assert.Equal(new Generation(1), Generation.Initial.Next());
        Assert.True(new Revision(2).CompareTo(new Revision(1)) > 0);
        Assert.True(new Generation(2).CompareTo(new Generation(1)) > 0);
    }

    [Fact]
    public void Revision_and_generation_fail_at_overflow_boundary()
    {
        Assert.Throws<InvalidOperationException>(() => new Revision(ulong.MaxValue).Next());
        Assert.Throws<InvalidOperationException>(() => new Generation(ulong.MaxValue).Next());
    }

    [Fact]
    public void Revision_and_generation_format_invariantly()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("123456", new Revision(123456).ToString());
            Assert.Equal("654321", new Generation(654321).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void Utc_timestamp_normalizes_offset_and_round_trips()
    {
        var timestamp = new UtcTimestamp(new DateTimeOffset(2026, 9, 15, 10, 30, 0, TimeSpan.FromHours(2)));
        var serialized = timestamp.ToString();
        var roundTrip = UtcTimestamp.Parse(serialized);

        Assert.Equal(TimeSpan.Zero, timestamp.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 8, 30, 0, TimeSpan.Zero), timestamp.Value);
        Assert.Equal(timestamp, roundTrip);
    }

    [Fact]
    public void Duration_uses_explicit_100_nanosecond_ticks()
    {
        var duration = Duration.FromTimeSpan(TimeSpan.FromMilliseconds(1));

        Assert.Equal(10_000, duration.Ticks);
        Assert.Equal(TimeSpan.FromMilliseconds(1), duration.ToTimeSpan());
        Assert.Equal(duration, Duration.Parse(duration.ToString()));
    }

    [Fact]
    public void Rational_reduces_to_canonical_value_equality()
    {
        Assert.Equal(new Rational(1, 2), new Rational(2, 4));
        Assert.Equal(new Rational(-1, 2), new Rational(-2, 4));
        Assert.Equal("0/1", new Rational(0, 99).ToString());
    }

    [Fact]
    public void Rational_rejects_non_positive_denominator()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rational(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rational(1, -1));
        Assert.False(Rational.TryParse("1/0", out _));
    }

    [Fact]
    public void Frame_rate_normalizes_and_supports_v1_reference_rates()
    {
        Assert.Equal(new FrameRate(25, 1), new FrameRate(50, 2));
        Assert.Equal("50/1", FrameRate.Fps50.ToString());
        Assert.Equal("60000/1001", FrameRate.Fps59_94.ToString());
        Assert.Equal(FrameRate.Fps59_94, FrameRate.Parse("60000/1001"));
    }

    [Fact]
    public void Frame_rate_rejects_zero_or_negative_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRate(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRate(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRate(1, 0));
        Assert.False(FrameRate.TryParse("0/1", out _));
    }

    [Fact]
    public void Timebase_is_exact_and_canonical()
    {
        var timebase = new Timebase(2, 180_000);

        Assert.Equal(new Timebase(1, 90_000), timebase);
        Assert.Equal("1/90000", timebase.ToString());
        Assert.Equal(timebase, Timebase.Parse("1/90000"));
    }

    [Fact]
    public void Validation_result_snapshots_issues_and_preserves_order()
    {
        var first = new ValidationIssue("SPEC001", "First problem", "inputs[0]");
        var second = new ValidationIssue("SPEC002", "Second problem");
        var source = new[] { first, second };
        var result = ValidationResult.Invalid(source);
        source[0] = new ValidationIssue("CHANGED", "Changed after construction");

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Issues.Count);
        Assert.Equal(first, result.Issues[0]);
        Assert.Equal(second, result.Issues[1]);
        Assert.True(ValidationResult.Valid.IsValid);
        Assert.Empty(ValidationResult.Valid.Issues);
    }

    [Fact]
    public void Validation_and_failure_require_machine_readable_codes()
    {
        Assert.Throws<ArgumentException>(() => new ValidationIssue(" ", "message"));
        Assert.Throws<ArgumentException>(() => new ValidationIssue("CODE", " "));
        Assert.Throws<ArgumentException>(() => new Failure(" ", "message"));
        Assert.Throws<ArgumentException>(() => new Failure("CODE", " "));
    }

    [Fact]
    public void Failure_has_deterministic_value_equality_and_formatting()
    {
        var failure = new Failure("CORE001", "Something failed");

        Assert.Equal(new Failure("CORE001", "Something failed"), failure);
        Assert.Equal("CORE001: Something failed", failure.ToString());
        Assert.True(failure.IsDefined);
    }

    [Fact]
    public void Compatibility_version_round_trips_without_implying_policy()
    {
        var version = CompatibilityVersion.Parse("2.7");

        Assert.Equal(new CompatibilityVersion(2, 7), version);
        Assert.Equal("2.7", version.ToString());
        Assert.True(version.HasSameMajor(new CompatibilityVersion(2, 99)));
        Assert.False(version.HasSameMajor(new CompatibilityVersion(3, 0)));
        Assert.True(version.CompareTo(new CompatibilityVersion(2, 6)) > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2.3")]
    [InlineData("-1.2")]
    public void Compatibility_version_rejects_invalid_text(string value)
    {
        Assert.False(CompatibilityVersion.TryParse(value, out _));
    }
}
