namespace SesPoc.Api.Models;

public sealed record RegisterResponse(Guid Id, string Name, string Email, bool EmailSent, DateTime CreatedUtc);
