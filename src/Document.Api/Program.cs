using System.Text.Json.Serialization;
using Document.Api.Data;
using Document.Api.Models;
using Document.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0))));

builder.Services.AddSingleton<RabbitMqPublisher>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    for (int i = 1; i <= 5; i++)
    {
        try
        {
            db.Database.EnsureCreated();
            logger.LogInformation("Database initialized successfully.");
            break;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Waiting for MySQL to be ready (attempt {Attempt}/5): {Message}", i, ex.Message);
            Thread.Sleep(3000);
        }
    }
}

app.MapGet("/", () => Results.Ok(new { service = "Document.Api", status = "running" }));

// 1. POST /documents
app.MapPost("/documents", async (CreateDocumentRequest req, AppDbContext db, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Creator) || string.IsNullOrWhiteSpace(req.Approver))
        return Results.BadRequest(new { message = "Title, Creator, and Approver are required." });

    var doc = new DocumentItem
    {
        Title = req.Title,
        Content = req.Content,
        Creator = req.Creator,
        Approver = req.Approver,
        Status = DocumentStatus.Draft,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    doc.Histories.Add(new DocumentHistory
    {
        Action = "Created",
        Actor = req.Creator,
        Note = "Tạo tài liệu nháp mới",
        Timestamp = DateTime.UtcNow
    });

    db.Documents.Add(doc);
    await db.SaveChangesAsync();

    logger.LogInformation("[Document Created] ID={Id}, Title='{Title}', Creator={Creator}", doc.Id, doc.Title, doc.Creator);
    return Results.Created($"/documents/{doc.Id}", doc);
});

// 2. GET /documents
app.MapGet("/documents", async (DocumentStatus? status, string? approver, string? creator, AppDbContext db) =>
{
    var query = db.Documents.AsQueryable();

    if (status.HasValue)
        query = query.Where(d => d.Status == status.Value);

    if (!string.IsNullOrWhiteSpace(approver))
        query = query.Where(d => d.Approver.Contains(approver));

    if (!string.IsNullOrWhiteSpace(creator))
        query = query.Where(d => d.Creator.Contains(creator));

    var list = await query.OrderByDescending(d => d.UpdatedAt).ToListAsync();
    return Results.Ok(list);
});

// 3. GET /documents/{id}
app.MapGet("/documents/{id:int}", async (int id, AppDbContext db) =>
{
    var doc = await db.Documents
        .Include(d => d.Histories.OrderBy(h => h.Timestamp))
        .FirstOrDefaultAsync(d => d.Id == id);

    return doc is not null
        ? Results.Ok(doc)
        : Results.NotFound(new { message = $"Document with ID {id} not found." });
});

// 4. POST /documents/{id}/submit
app.MapPost("/documents/{id:int}/submit", async (int id, WorkflowActionRequest? req, AppDbContext db, RabbitMqPublisher publisher, ILogger<Program> logger) =>
{
    var doc = await db.Documents.Include(d => d.Histories).FirstOrDefaultAsync(d => d.Id == id);
    if (doc is null) return Results.NotFound(new { message = "Document not found." });

    if (doc.Status != DocumentStatus.Draft)
        return Results.BadRequest(new { message = $"Cannot submit document in status '{doc.Status}'. Only 'Draft' documents can be submitted." });

    var actor = string.IsNullOrWhiteSpace(req?.Actor) ? doc.Creator : req.Actor;
    var note = string.IsNullOrWhiteSpace(req?.Note) ? "Gửi tài liệu chờ phê duyệt" : req.Note;

    doc.Status = DocumentStatus.Submitted;
    doc.UpdatedAt = DateTime.UtcNow;
    doc.Histories.Add(new DocumentHistory
    {
        Action = "Submitted",
        Actor = actor,
        Note = note,
        Timestamp = DateTime.UtcNow
    });

    await db.SaveChangesAsync();

    await publisher.PublishAsync(new DocumentEventMessage(
        "DocumentSubmitted", doc.Id, doc.Title, doc.Creator, doc.Approver,
        doc.Status.ToString(), actor, note, DateTime.UtcNow));

    logger.LogInformation("[Document Submitted] ID={Id} by {Actor}", doc.Id, actor);
    return Results.Ok(doc);
});

// 5. POST /documents/{id}/approve
app.MapPost("/documents/{id:int}/approve", async (int id, WorkflowActionRequest? req, AppDbContext db, RabbitMqPublisher publisher, ILogger<Program> logger) =>
{
    var doc = await db.Documents.Include(d => d.Histories).FirstOrDefaultAsync(d => d.Id == id);
    if (doc is null) return Results.NotFound(new { message = "Document not found." });

    if (doc.Status != DocumentStatus.Submitted)
        return Results.BadRequest(new { message = $"Cannot approve document in status '{doc.Status}'. Only 'Submitted' documents can be approved." });

    var actor = string.IsNullOrWhiteSpace(req?.Actor) ? doc.Approver : req.Actor;
    var note = string.IsNullOrWhiteSpace(req?.Note) ? "Đã phê duyệt tài liệu" : req.Note;

    doc.Status = DocumentStatus.Approved;
    doc.UpdatedAt = DateTime.UtcNow;
    doc.Histories.Add(new DocumentHistory
    {
        Action = "Approved",
        Actor = actor,
        Note = note,
        Timestamp = DateTime.UtcNow
    });

    await db.SaveChangesAsync();

    await publisher.PublishAsync(new DocumentEventMessage(
        "DocumentApproved", doc.Id, doc.Title, doc.Creator, doc.Approver,
        doc.Status.ToString(), actor, note, DateTime.UtcNow));

    logger.LogInformation("[Document Approved] ID={Id} by {Actor}", doc.Id, actor);
    return Results.Ok(doc);
});

// 6. POST /documents/{id}/reject
app.MapPost("/documents/{id:int}/reject", async (int id, WorkflowActionRequest? req, AppDbContext db, RabbitMqPublisher publisher, ILogger<Program> logger) =>
{
    var doc = await db.Documents.Include(d => d.Histories).FirstOrDefaultAsync(d => d.Id == id);
    if (doc is null) return Results.NotFound(new { message = "Document not found." });

    if (doc.Status != DocumentStatus.Submitted)
        return Results.BadRequest(new { message = $"Cannot reject document in status '{doc.Status}'. Only 'Submitted' documents can be rejected." });

    var actor = string.IsNullOrWhiteSpace(req?.Actor) ? doc.Approver : req.Actor;
    var note = string.IsNullOrWhiteSpace(req?.Note) ? "Từ chối phê duyệt tài liệu" : req.Note;

    doc.Status = DocumentStatus.Rejected;
    doc.UpdatedAt = DateTime.UtcNow;
    doc.Histories.Add(new DocumentHistory
    {
        Action = "Rejected",
        Actor = actor,
        Note = note,
        Timestamp = DateTime.UtcNow
    });

    await db.SaveChangesAsync();

    await publisher.PublishAsync(new DocumentEventMessage(
        "DocumentRejected", doc.Id, doc.Title, doc.Creator, doc.Approver,
        doc.Status.ToString(), actor, note, DateTime.UtcNow));

    logger.LogInformation("[Document Rejected] ID={Id} by {Actor}", doc.Id, actor);
    return Results.Ok(doc);
});

app.Run();