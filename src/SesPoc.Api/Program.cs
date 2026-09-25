using System.ComponentModel.DataAnnotations;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SesPoc.Api.Data;
using SesPoc.Api.Models;
using SesPoc.Api.Options;
using SesPoc.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<SesOptions>()
    .Bind(builder.Configuration.GetSection(SesOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("SesPoc"));

// SES client that authenticates ONLY via the EC2 instance profile (IAM role) through IMDS.
// The default credential chain is deliberately bypassed so access keys in env vars,
// appsettings or ~/.aws/credentials can never be picked up.
// Created lazily because InstanceProfileAWSCredentials contacts IMDS in its constructor; PublicationOnly
// means a failure (e.g. no role attached yet) is not cached and the next send retries.
builder.Services.AddSingleton(sp => new Lazy<IAmazonSimpleEmailServiceV2>(() =>
{
    var ses = sp.GetRequiredService<IOptions<SesOptions>>().Value;
    return new AmazonSimpleEmailServiceV2Client(
        new InstanceProfileAWSCredentials(),
        RegionEndpoint.GetBySystemName(ses.Region));
}, LazyThreadSafetyMode.PublicationOnly));
builder.Services.AddScoped<IEmailSender, SesEmailSender>();

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Swagger is enabled in all environments for this POC (IIS runs as Production).
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Redirect("swagger")).ExcludeFromDescription();

var users = app.MapGroup("/api/users").WithTags("Users");

users.MapPost("/register", async (
        RegisterRequest request,
        AppDbContext db,
        IEmailSender emailSender,
        ILogger<Program> logger,
        CancellationToken ct) =>
    {
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
        {
            var errors = validationResults
                .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty), (r, member) => (Member: member, r.ErrorMessage))
                .GroupBy(x => x.Member)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid value.").ToArray());
            return Results.ValidationProblem(errors);
        }

        var name = request.Name.Trim();
        var email = request.Email.Trim().ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Email already registered",
                detail: $"A user with email '{email}' already exists.");
        }

        // Send first; the user is persisted only if SES accepted the email (rollback semantics).
        try
        {
            await emailSender.SendWelcomeAsync(name, email, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send welcome email to {Email}; registration rolled back", email);
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Failed to send welcome email",
                detail: $"Registration was not saved. {ex.GetType().Name}: {ex.Message}");
        }

        var user = new User { Id = Guid.NewGuid(), Name = name, Email = email, CreatedUtc = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/users/{user.Id}",
            new RegisterResponse(user.Id, user.Name, user.Email, EmailSent: true, user.CreatedUtc));
    })
    .WithName("RegisterUser")
    .Produces<RegisterResponse>(StatusCodes.Status201Created)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status502BadGateway);

users.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        await db.Users.AsNoTracking().OrderBy(u => u.CreatedUtc).ToListAsync(ct))
    .WithName("ListUsers")
    .Produces<List<User>>();

app.Run();
