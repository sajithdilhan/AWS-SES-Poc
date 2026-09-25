using System.ComponentModel.DataAnnotations;

namespace SesPoc.Api.Options;

/// <summary>
/// Non-secret SES settings. AWS credentials are intentionally NOT configurable here;
/// they come exclusively from the EC2 instance profile (IAM role).
/// </summary>
public sealed class SesOptions
{
    public const string SectionName = "Ses";

    [Required]
    public string Region { get; set; } = string.Empty;

    [Required, EmailAddress]
    public string FromAddress { get; set; } = string.Empty;
}
