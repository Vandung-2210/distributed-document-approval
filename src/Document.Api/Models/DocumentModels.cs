namespace Document.Api.Models;

public enum DocumentStatus
{
    Draft,
    Submitted,
    Approved,
    Rejected
}

public class DocumentItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public string Approver { get; set; } = string.Empty;
    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<DocumentHistory> Histories { get; set; } = new();
}

public class DocumentHistory
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public string Action { get; set; } = string.Empty; // Created, Submitted, Approved, Rejected
    public string Actor { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// DTOs nhận dữ liệu từ Client
public record CreateDocumentRequest(string Title, string Content, string Creator, string Approver);
public record WorkflowActionRequest(string Actor, string? Note);