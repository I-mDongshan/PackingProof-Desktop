using System;

namespace ExpressPackingMonitoring.Data;

public partial class VideoDatabase
{
    /// <summary>
    /// 用户删除完成（本地与网络端均已删除或无需删除）后标记记录删除并写入删除日志。
    /// </summary>
    public bool MarkRecordDeletedById(long recordId, string reason, string reasonCode = "")
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                int updated;
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        UPDATE VideoRecords SET
                            IsDeleted = 1,
                            DeletedAt = @deletedAt,
                            DeleteReason = @reason,
                            DeleteReasonCode = @reasonCode,
                            ArchiveStatus = @archiveStatus
                        WHERE Id = @id AND IsDeleted = 0;";
                    cmd.Parameters.AddWithValue("@deletedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    cmd.Parameters.AddWithValue("@reason", reason ?? "");
                    cmd.Parameters.AddWithValue("@reasonCode", reasonCode?.Trim() ?? "");
                    cmd.Parameters.AddWithValue("@archiveStatus", VideoArchiveStatus.LocalDeleted);
                    cmd.Parameters.AddWithValue("@id", recordId);
                    updated = cmd.ExecuteNonQuery();
                }

                if (updated == 0)
                {
                    transaction.Rollback();
                    return false;
                }

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO DeleteLogs (FilePath, OrderId, FileSizeBytes, DeletedAt, Reason)
                        SELECT FilePath, OrderId, FileSizeBytes, @deletedAt, @reason
                        FROM VideoRecords WHERE Id = @id
                        LIMIT 1;";
                    cmd.Parameters.AddWithValue("@deletedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    cmd.Parameters.AddWithValue("@reason", reason ?? "");
                    cmd.Parameters.AddWithValue("@id", recordId);
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                return true;
            }
            catch
            {
                try { transaction.Rollback(); } catch { }
                return false;
            }
        }
    }
}
