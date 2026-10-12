using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace WaxyCandles.Infrastructure.Persistence;

public static class DbUpdateExceptionExtensions
{
    // SQL Server: 2627 = primary key / unique constraint, 2601 = unique index.
    public static bool IsUniqueConstraintViolation(this DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2627 or 2601 };

    // SQL Server: 547 = constraint conflict. It is also raised for CHECK
    // constraints and every other foreign key, so match the constraint by name.
    public static bool IsForeignKeyViolation(
        this DbUpdateException exception,
        string constraintName) =>
        exception.InnerException is SqlException { Number: 547 } sql &&
        sql.Message.Contains(constraintName, StringComparison.OrdinalIgnoreCase);
}
