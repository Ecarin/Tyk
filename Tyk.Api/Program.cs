using Microsoft.EntityFrameworkCore;
using Tyk.Application.Interfaces;
using Tyk.Application.Services;
using Tyk.Infrastructure.Data;
using Tyk.Infrastructure.Repositories;
using DotNetEnv;
using Tyk.Domain.Interfaces;

Env.Load();

var builder = WebApplication.CreateBuilder(args);

// Set up logging
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Debug);

// Database Configuration using .env variables
builder.Services.AddDbContext<TimeTrackerContext>(options =>
    options.UseSqlite(Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING")));

// Interface Registrations
builder.Services.AddScoped<ITimeTrackerContext>(provider =>
    provider.GetRequiredService<TimeTrackerContext>());
builder.Services.AddScoped<ITimeEntryRepository, TimeEntryRepository>();

// Register Timer as a Singleton or Transient
builder.Services.AddSingleton<Timer>(provider =>
{
    return new Timer(_ => { }, null, TimeSpan.Zero, TimeSpan.FromMinutes(1)); // Timer logic
});

// Hosted Service with Correct Scope Handling
builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddHostedService(provider =>
    provider.GetRequiredService<TelegramBotService>());

// API Configuration
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
// builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    // app.UseSwagger();
    // app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Apply database migrations
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<TimeTrackerContext>();
    dbContext.Database.Migrate();
}

app.Run();