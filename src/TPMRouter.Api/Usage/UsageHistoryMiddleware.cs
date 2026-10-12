using System.Text;
using Microsoft.Data.Sqlite;
using TPMRouter.Api.Auth;

namespace TPMRouter.Api.Usage;

public sealed class UsageHistoryMiddleware(RequestDelegate next, ILogger<UsageHistoryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, UsageHistoryStore store, TimeProvider timeProvider)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ApiKeyRequirement>()
            is not { Scope: ApiKeyScope.Client, OpenAICompatible: true })
        {
            await next(context);
            return;
        }

        DateTimeOffset timestamp = timeProvider.GetUtcNow();
        context.Request.EnableBuffering();
        using StreamReader reader = new(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        string request = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;
        UsageRecordBuilder record = new(request);
        Stream original = context.Response.Body;
        using MemoryStream captured = new();
        using RecordingStream recording = new(original, captured);
        context.Response.Body = recording;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = original;
            record.ReadResponse(Encoding.UTF8.GetString(captured.GetBuffer(), 0, (int)captured.Length),
                                context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);
            try
            {
                store.Save(record.Build(timestamp, context.Connection.RemoteIpAddress?.ToString() ?? ""));
            }
            catch (SqliteException ex)
            {
                logger.LogError(ex, "Could not save usage history.");
            }
        }
    }

    private sealed class RecordingStream(Stream inner, Stream captured) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            captured.Write(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            captured.Write(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }
    }
}
