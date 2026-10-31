using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var store = new CardStore(Path.Combine(app.Environment.ContentRootPath, "data", "cards.json"));

app.MapGet("/api/cards", (string? search, string? status) =>
{
    var cards = store.All();
    if (!string.IsNullOrWhiteSpace(search))
        cards = cards.Where(c => c.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
            || c.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
            || c.Category.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
    if (!string.IsNullOrWhiteSpace(status))
        cards = cards.Where(c => string.Equals(c.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
    return Results.Ok(cards.OrderByDescending(c => c.CreatedAtUtc));
});

app.MapGet("/api/stats", () =>
{
    var all = store.All();
    return Results.Ok(new
    {
        total = all.Count,
        todo = all.Count(c => c.Status == "todo"),
        progress = all.Count(c => c.Status == "progress"),
        done = all.Count(c => c.Status == "done"),
        completion = all.Count == 0 ? 0 : Math.Round(all.Count(c => c.Status == "done") * 100.0 / all.Count)
    });
});

app.MapPost("/api/cards", (NewCard input) =>
{
    if (!CardStore.IsValidTitle(input.Title))
        return Results.BadRequest(new { error = "Název musí mít 1 až 80 znaků." });
    if (input.Description?.Length > 500 || input.Category?.Length > 25)
        return Results.BadRequest(new { error = "Popis nebo kategorie jsou příliš dlouhé." });
    if (!CardStore.IsPriority(input.Priority ?? "medium"))
        return Results.BadRequest(new { error = "Neplatná priorita." });
    var card = store.Add(input);
    return Results.Created($"/api/cards/{card.Id}", card);
});

app.MapPut("/api/cards/{id:guid}", (Guid id, EditCard input) =>
{
    if (input.Title is not null && !CardStore.IsValidTitle(input.Title))
        return Results.BadRequest(new { error = "Název musí mít 1 až 80 znaků." });
    if (input.Description?.Length > 500 || input.Category?.Length > 25)
        return Results.BadRequest(new { error = "Popis nebo kategorie jsou příliš dlouhé." });
    if (input.Status is not null && !CardStore.IsStatus(input.Status))
        return Results.BadRequest(new { error = "Neznámý sloupec." });
    if (input.Priority is not null && !CardStore.IsPriority(input.Priority))
        return Results.BadRequest(new { error = "Neplatná priorita." });
    var result = store.Update(id, input);
    return result.Error switch
    {
        "not_found" => Results.NotFound(new { error = "Karta neexistuje." }),
        "wip_limit" => Results.Conflict(new { error = "Limit rozpracovaných karet je 3." }),
        _ => Results.Ok(result.Card)
    };
});

app.MapDelete("/api/cards/{id:guid}", (Guid id) =>
    store.Delete(id) ? Results.NoContent() : Results.NotFound(new { error = "Karta neexistuje." }));

app.Run();

public sealed record Card(Guid Id, string Title, string Description, string Status,
    string Priority, string Category, DateTime CreatedAtUtc);
public sealed record NewCard(string Title, string? Description, string? Priority, string? Category);
public sealed record EditCard(string? Title, string? Description, string? Status, string? Priority, string? Category);

public sealed class CardStore
{
    private readonly object _lock = new();
    private readonly string _file;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly List<Card> _cards;

    public CardStore(string file)
    {
        _file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file))
        {
            _cards = JsonSerializer.Deserialize<List<Card>>(File.ReadAllText(file), _json)
                ?? new List<Card>();
        }
        else
        {
            _cards = new List<Card>
            {
                Seed("Navrhnout přehled projektu", "Wireframe a design rozhraní", "done", "high", "Design"),
                Seed("Připravit REST API", "Endpointy pro správu karet", "progress", "high", "Backend"),
                Seed("Napsat testovací scénáře", "Otestovat základní workflow", "todo", "medium", "QA"),
                Seed("Doplnit dokumentaci", "README, instalace a ukázky", "todo", "low", "Docs"),
            };
            Persist();
        }
    }

    private static Card Seed(string title, string description, string status, string priority, string category) =>
        new(Guid.NewGuid(), title, description, status, priority, category, DateTime.UtcNow);

    public List<Card> All() { lock (_lock) return _cards.ToList(); }
    public static bool IsValidTitle(string? title) => !string.IsNullOrWhiteSpace(title) && title.Trim().Length <= 80;
    public static bool IsStatus(string s) => s is "todo" or "progress" or "done";
    public static bool IsPriority(string s) => s is "low" or "medium" or "high";

    public Card Add(NewCard input)
    {
        lock (_lock)
        {
            var card = new Card(Guid.NewGuid(), input.Title.Trim(), input.Description?.Trim() ?? "",
                "todo", input.Priority ?? "medium", input.Category?.Trim() ?? "Obecné", DateTime.UtcNow);
            _cards.Add(card);
            Persist();
            return card;
        }
    }

    public (Card? Card, string? Error) Update(Guid id, EditCard input)
    {
        lock (_lock)
        {
            var index = _cards.FindIndex(c => c.Id == id);
            if (index < 0) return (null, "not_found");
            var old = _cards[index];
            var targetStatus = input.Status ?? old.Status;
            if (old.Status != "progress" && targetStatus == "progress" && _cards.Count(c => c.Status == "progress") >= 3)
                return (null, "wip_limit");
            var updated = old with
            {
                Title = input.Title?.Trim() ?? old.Title,
                Description = input.Description?.Trim() ?? old.Description,
                Status = targetStatus,
                Priority = input.Priority ?? old.Priority,
                Category = input.Category?.Trim() ?? old.Category
            };
            _cards[index] = updated;
            Persist();
            return (updated, null);
        }
    }

    public bool Delete(Guid id)
    {
        lock (_lock)
        {
            var removed = _cards.RemoveAll(c => c.Id == id) > 0;
            if (removed) Persist();
            return removed;
        }
    }

    private void Persist()
    {
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_cards, _json));
        File.Move(temporary, _file, true);
    }
}
