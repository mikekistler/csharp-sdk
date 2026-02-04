using ModelContextProtocol.Client;

namespace ModelContextProtocol.Tests.Client;

public class McpHeaderEncoderTests
{
    [Theory]
    [InlineData("us-west1", "us-west1")]
    [InlineData("hello world", "hello world")]
    [InlineData("test-value_123", "test-value_123")]
    [InlineData("", "")]
    public void EncodeValue_PlainAscii_ReturnsUnchanged(string input, string expected)
    {
        var result = McpHeaderEncoder.EncodeValue(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(" leadingSpace", "=?base64?IGxlYWRpbmdTcGFjZQ==?=")]
    [InlineData("trailingSpace ", "=?base64?dHJhaWxpbmdTcGFjZSA=?=")]
    [InlineData(" both ", "=?base64?IGJvdGgg?=")]
    [InlineData("\tleadingTab", "=?base64?CWxlYWRpbmdUYWI=?=")]
    public void EncodeValue_LeadingTrailingWhitespace_ReturnsBase64Encoded(string input, string expected)
    {
        var result = McpHeaderEncoder.EncodeValue(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Hello, 世界")]
    [InlineData("日本語")]
    [InlineData("café")]
    public void EncodeValue_NonAsciiCharacters_ReturnsBase64Encoded(string input)
    {
        var result = McpHeaderEncoder.EncodeValue(input);
        Assert.NotNull(result);
        Assert.StartsWith("=?base64?", result);
        Assert.EndsWith("?=", result);
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\r\nline2")]
    [InlineData("with\0null")]
    public void EncodeValue_ControlCharacters_ReturnsBase64Encoded(string input)
    {
        var result = McpHeaderEncoder.EncodeValue(input);
        Assert.NotNull(result);
        Assert.StartsWith("=?base64?", result);
        Assert.EndsWith("?=", result);
    }

    [Fact]
    public void EncodeValue_ExceedsMaxLength_ReturnsNull()
    {
        var longValue = new string('a', McpHeaderEncoder.MaxHeaderValueLength + 1);
        var result = McpHeaderEncoder.EncodeValue(longValue);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void EncodeValue_Boolean_ReturnsLowercaseString(bool input, string expected)
    {
        var result = McpHeaderEncoder.EncodeValue(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(42, "42")]
    [InlineData(3.14, "3.14")]
    [InlineData(-100, "-100")]
    [InlineData(0, "0")]
    public void EncodeValue_Numbers_ReturnsStringRepresentation(object input, string expected)
    {
        var result = McpHeaderEncoder.EncodeValue(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EncodeValue_Null_ReturnsNull()
    {
        var result = McpHeaderEncoder.EncodeValue(null);
        Assert.Null(result);
    }

    [Fact]
    public void EncodeValue_UnsupportedType_ReturnsNull()
    {
        var result = McpHeaderEncoder.EncodeValue(new object());
        Assert.Null(result);
    }

    [Theory]
    [InlineData("us-west1", "us-west1")]
    [InlineData("=?base64?SGVsbG8=?=", "Hello")]
    [InlineData("=?base64?5pel5pys6Kqe?=", "日本語")]
    [InlineData("=?BASE64?SGVsbG8=?=", "Hello")] // Case-insensitive prefix
    public void DecodeValue_ValidValues_ReturnsDecodedString(string input, string expected)
    {
        var result = McpHeaderEncoder.DecodeValue(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("=?base64?invalid!base64?=")]
    [InlineData("=?base64?????=")]
    public void DecodeValue_InvalidBase64_ReturnsNull(string input)
    {
        var result = McpHeaderEncoder.DecodeValue(input);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void DecodeValue_NullOrEmpty_ReturnsSameValue(string? input)
    {
        var result = McpHeaderEncoder.DecodeValue(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void DecodeValue_MissingWrapper_ReturnsOriginal()
    {
        // Missing suffix
        var result1 = McpHeaderEncoder.DecodeValue("=?base64?SGVsbG8=");
        Assert.Equal("=?base64?SGVsbG8=", result1);

        // Missing prefix
        var result2 = McpHeaderEncoder.DecodeValue("SGVsbG8=?=");
        Assert.Equal("SGVsbG8=?=", result2);
    }

    [Fact]
    public void EncodeValue_ThenDecodeValue_RoundTrips()
    {
        var testValues = new[]
        {
            "simple",
            " leading",
            "trailing ",
            "Hello, 世界",
            "line1\nline2",
            "42",
            "true"
        };

        foreach (var original in testValues)
        {
            var encoded = McpHeaderEncoder.EncodeValue(original);
            Assert.NotNull(encoded);
            var decoded = McpHeaderEncoder.DecodeValue(encoded);
            Assert.Equal(original, decoded);
        }
    }
}
