using System.Globalization;
using ConsoleApp1.Data;
using ConsoleApp1.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.OutputCaching;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PostsDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Posts") ?? "Data Source=posts.db"));

builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("PostById", policy => policy
        .Expire(TimeSpan.FromMinutes(10))
        .Tag("posts"));
});

var app = builder.Build();

app.UseOutputCache();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PostsDbContext>();
    await db.Database.EnsureCreatedAsync();

    if (!await db.Posts.AnyAsync())
    {
        db.Posts.AddRange(
            new Post
            {
                Title = "First sample post",
                Content = "Edit this post with PUT /posts/1.",
                UpdatedAt = DateTime.UtcNow
            },
            new Post
            {
                Title = "Second sample post",
                Content = "Another sample row in SQLite.",
                UpdatedAt = DateTime.UtcNow
            });

        await db.SaveChangesAsync();
    }
}

app.MapGet("/posts/{id:int}", async (
    int id,
    HttpContext http,
    PostsDbContext db,
    ILogger<Program> logger) =>
{
    logger.LogInformation("DATABASE QUERY: loading Post {PostId} from SQLite", id);

    var post = await db.Posts
        .AsNoTracking()
        .SingleOrDefaultAsync(p => p.Id == id);

    if (post is null)
    {
        logger.LogInformation("DATABASE RESULT: Post {PostId} was not found", id);
        return Results.NotFound();
    }

    var lastModified = new DateTimeOffset(
        DateTime.SpecifyKind(post.UpdatedAt, DateTimeKind.Utc));

    http.Response.Headers.LastModified =
        lastModified.ToString("R", CultureInfo.InvariantCulture);

    return Results.Ok(post);
})
.CacheOutput("PostById");

app.MapPut("/posts/{id:int}", async (
    int id,
    UpdatePostRequest request,
    HttpContext httpContext,
    PostsDbContext db,
    IOutputCacheStore outputCache,
    ILogger<Program> logger) =>
{
    var post = await db.Posts.SingleOrDefaultAsync(p => p.Id == id);

    if (post is null)
        return Results.NotFound();

    post.Title = request.Title;
    post.Content = request.Content;

    var now = DateTime.UtcNow;

    var previousHttpDate = new DateTime(
        post.UpdatedAt.Ticks - post.UpdatedAt.Ticks % TimeSpan.TicksPerSecond,
        DateTimeKind.Utc);

    post.UpdatedAt = now > previousHttpDate
        ? now
        : previousHttpDate.AddSeconds(1);

    await db.SaveChangesAsync();

    await outputCache.EvictByTagAsync(
        "posts",
        httpContext.RequestAborted);

    logger.LogInformation(
        "CACHE EVICTED: removed cached GET response for Post {PostId}",
        id);

    return Results.Ok(post);
});

app.Run();

public sealed record UpdatePostRequest(string Title, string Content);