using System.Security.Cryptography;
using DeskFlow.Core.Abstractions;
using DeskFlow.Core.Models;
using DeskFlow.Core.Runtime;

namespace DeskFlow.Infrastructure.Steps;

/// <summary>计算 SHA-256（大文件只算前 8MB 并标记 partial），用于去重与变更检测。</summary>
public sealed class FileHashStep : IPipelineStep
{
    private const long FullHashLimit = 256L * 1024 * 1024;
    private const int PartialHashBytes = 8 * 1024 * 1024;

    public string Name => "hash";

    public string Description => "计算文件指纹（SHA-256），大文件只算前 8MB";

    public async Task<StepOutcome> ExecuteAsync(ProcessedItem item, JobContext context, CancellationToken cancellationToken)
    {
        var path = item.Source.FullPath;
        var length = item.Source.SizeBytes;
        var partial = length > FullHashLimit;

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 128 * 1024,
                useAsync: true);

            using var sha = SHA256.Create();
            if (partial)
            {
                var buffer = new byte[256 * 1024];
                var remaining = PartialHashBytes;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    sha.TransformBlock(buffer, 0, read, null, 0);
                    remaining -= read;
                }

                sha.TransformFinalBlock([], 0, 0);
            }

            var hash = partial
                ? Convert.ToHexString(sha.Hash!).ToLowerInvariant()
                : Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();

            item.Set("hash.sha256", hash);
            item.Set("hash.mode", partial ? "partial-8mb" : "full");
            item.Set("hash.bytes", length.ToString());
            return StepOutcome.Success(partial ? "大文件：指纹为前 8MB" : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StepOutcome.Failure(ex.Message);
        }
    }
}
