using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// === JWT config ===
var jwtKey = Environment.GetEnvironmentVariable("JWT_KEY") ?? builder.Configuration["Jwt:Key"] ?? "my_secret_key";
var jwtIssuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? builder.Configuration["Jwt:Issuer"] ?? "api.todolist";
var jwtAudience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? builder.Configuration["Jwt:Audience"] ?? "api.todolist.clients";
var jwtExpiresMinStr = Environment.GetEnvironmentVariable("JWT_EXPIRES_MIN") ?? builder.Configuration["Jwt:ExpiresMinutes"];
var jwtExpiresMinutes = int.TryParse(jwtExpiresMinStr, out var mins) ? mins : 60;
var demoUser = Environment.GetEnvironmentVariable("JWT_DEMO_USER");
var demoPass = Environment.GetEnvironmentVariable("JWT_DEMO_PASS");

var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = key
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// === MongoDB config ===
var mongoConn = Environment.GetEnvironmentVariable("MONGODB_URI") 
                ?? builder.Configuration["MongoDb:ConnectionString"] 
                ?? "mongodb://localhost:27017";
var mongoDbName = Environment.GetEnvironmentVariable("MONGODB_DB") 
                ?? builder.Configuration["MongoDb:DatabaseName"] 
                ?? "todo_db";

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConn));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(mongoDbName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<TaskItem>("tasks"));

// === App ===
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

// === Auto-migrate: validator + counter + index(Unique on Id) ===
if (builder.Configuration.GetValue("AutoMigrate", true))
{
    var db = app.Services.GetRequiredService<IMongoDatabase>();

    // Apply/ensure JSON Schema validator
    var validator = new BsonDocument {
      { "$jsonSchema", new BsonDocument {
        { "bsonType", "object" },
        { "required", new BsonArray { "Id", "Title", "Completed" } },
        { "additionalProperties", false },
        { "properties", new BsonDocument {
            { "Id",         new BsonDocument { { "bsonType", "int" } } },
            { "Title",      new BsonDocument { { "bsonType", "string" }, { "maxLength", 200 } } },
            { "Description",new BsonDocument { { "bsonType", new BsonArray { "string", "null" } } } },
            { "Completed",  new BsonDocument { { "bsonType", "bool" } } }
        } }
      } }
    };

    var names = await db.ListCollectionNames().ToListAsync();
    if (!names.Contains("tasks"))
    {
        await db.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "create", "tasks" },
            { "validator", validator },
            { "validationLevel", "strict" },
            { "validationAction", "error" }
        });
    }
    else
    {
        await db.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", "tasks" },
            { "validator", validator },
            { "validationLevel", "strict" },
            { "validationAction", "error" }
        });
    }

    // Ensure counters doc exists and align seq to current max(Id)
    var counters = db.GetCollection<Counter>("counters");
    var counter = await counters.Find(x => x.Id == "tasks").FirstOrDefaultAsync();
    var tasksCol = db.GetCollection<TaskItem>("tasks");
    var last = await tasksCol.Find(FilterDefinition<TaskItem>.Empty)
                             .SortByDescending(x => x.Id).Limit(1).FirstOrDefaultAsync();
    var maxId = last?.Id ?? 0;
    if (counter is null || counter.Seq < maxId)
    {
        await counters.UpdateOneAsync(
            Builders<Counter>.Filter.Eq(x => x.Id, "tasks"),
            Builders<Counter>.Update.Set(x => x.Seq, maxId),
            new UpdateOptions { IsUpsert = true }
        );
    }

    // Ensure unique index on Id
    var keys = Builders<TaskItem>.IndexKeys.Ascending(x => x.Id);
    var idxModel = new CreateIndexModel<TaskItem>(keys, new CreateIndexOptions { Unique = true, Name = "ux_tasks_Id" });
    await tasksCol.Indexes.CreateOneAsync(idxModel);
}

// === Endpoints ===

// Login -> returns JWT; if DEMO_* are set, enforce them
app.MapPost("/login", (LoginRequest req) =>
{
    if (!string.IsNullOrWhiteSpace(demoUser) || !string.IsNullOrWhiteSpace(demoPass))
    {
        if (req is null || req.Username != demoUser || req.Password != demoPass)
            return Results.Unauthorized();
    }
    else
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return Results.BadRequest(new { message = "username/password required" });
    }

    var claims = new[] { new System.Security.Claims.Claim("name", req.Username) };
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
        issuer: jwtIssuer,
        audience: jwtAudience,
        claims: claims,
        expires: DateTime.UtcNow.AddMinutes(jwtExpiresMinutes),
        signingCredentials: creds);

    var tokenStr = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    return Results.Ok(new { token = tokenStr });
});

var tasks = app.MapGroup("/").RequireAuthorization();

// Create
tasks.MapPost("/tasks", async (IMongoDatabase db, TaskItem input) =>
{
    if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 200)
        return Results.BadRequest(new { message = "Title é obrigatório (≤ 200)." });

    var col = db.GetCollection<TaskItem>("tasks");

    // get next sequence (atomic)
    var counters = db.GetCollection<Counter>("counters");
    var next = await counters.FindOneAndUpdateAsync(
        Builders<Counter>.Filter.Eq(x => x.Id, "tasks"),
        Builders<Counter>.Update.Inc(x => x.Seq, 1),
        new FindOneAndUpdateOptions<Counter, Counter> { ReturnDocument = ReturnDocument.After, IsUpsert = true });

    input.Id = next.Seq;
    await col.InsertOneAsync(input);
    return Results.Created($"/tasks/{input.Id}", input);
});

// List
tasks.MapGet("/tasks", async (IMongoDatabase db) =>
{
    var col = db.GetCollection<TaskItem>("tasks");
    var list = await col.Find(FilterDefinition<TaskItem>.Empty).SortByDescending(x => x.Id).ToListAsync();
    return Results.Ok(list);
});

// Get by id
tasks.MapGet("/tasks/{id:int}", async (IMongoDatabase db, int id) =>
{
    var col = db.GetCollection<TaskItem>("tasks");
    var item = await col.Find(x => x.Id == id).FirstOrDefaultAsync();
    return item is null ? Results.NotFound() : Results.Ok(item);
});

// Update
tasks.MapPut("/tasks/{id:int}", async (IMongoDatabase db, int id, TaskItem input) =>
{
    if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 200)
        return Results.BadRequest(new { message = "Title é obrigatório (≤ 200)." });

    var col = db.GetCollection<TaskItem>("tasks");
    input.Id = id;
    var res = await col.ReplaceOneAsync(x => x.Id == id, input);
    if (res.MatchedCount == 0) return Results.NotFound();
    return Results.Ok(input);
});

// Delete
tasks.MapDelete("/tasks/{id:int}", async (IMongoDatabase db, int id) =>
{
    var col = db.GetCollection<TaskItem>("tasks");
    var res = await col.DeleteOneAsync(x => x.Id == id);
    if (res.DeletedCount == 0) return Results.NotFound();
    return Results.Ok(new { message = "Task deleted" });
});

app.Run();

// === Models ===
record LoginRequest(string Username, string Password);

class TaskItem
{
    [BsonId]
    public ObjectId _id { get; set; }  // Mongo internal id

    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Completed { get; set; }
}

class Counter
{
    [BsonId]
    public string Id { get; set; } = default!; // e.g., "tasks"
    public int Seq { get; set; }
}
