using System.Net;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Options;
using SesPoc.Api.Options;

namespace SesPoc.Api.Services;

public sealed class SesEmailSender(
    Lazy<IAmazonSimpleEmailServiceV2> ses,
    IOptions<SesOptions> options,
    ILogger<SesEmailSender> logger) : IEmailSender
{
    public async Task<string> SendWelcomeAsync(string name, string email, CancellationToken cancellationToken = default)
    {
        var safeName = WebUtility.HtmlEncode(name);

        var request = new SendEmailRequest
        {
            FromEmailAddress = options.Value.FromAddress,
            Destination = new Destination { ToAddresses = [email] },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = "Welcome! Your registration was successful" },
                    Body = new Body
                    {
                        Html = new Content { Data = $"<h2>Welcome, {safeName}!</h2><p>Thanks for registering. Your account has been created.</p>" },
                        Text = new Content { Data = $"Welcome, {name}!\n\nThanks for registering. Your account has been created." }
                    }
                }
            }
        };

        var response = await ses.Value.SendEmailAsync(request, cancellationToken);
        logger.LogInformation("SES accepted welcome email to {Email}. MessageId: {MessageId}", email, response.MessageId);
        return response.MessageId;
    }
}
