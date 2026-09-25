namespace SesPoc.Api.Services;

public interface IEmailSender
{
    /// <summary>Sends the welcome email and returns the provider message id.</summary>
    Task<string> SendWelcomeAsync(string name, string email, CancellationToken cancellationToken = default);
}
