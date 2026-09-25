using System.ComponentModel.DataAnnotations;

namespace SesPoc.Api.Models;

public sealed class RegisterRequest
{
    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; set; } = string.Empty;
}
