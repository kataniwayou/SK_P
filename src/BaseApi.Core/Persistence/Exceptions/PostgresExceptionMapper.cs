using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BaseApi.Core.Persistence.Exceptions;

/// <summary>
/// Translates EF Core <see cref="DbUpdateException"/> wrapping a
/// <see cref="PostgresException"/> into an HTTP-friendly (status, detail, column)
/// triple. Pure helper — no DI, no async, unit-testable in isolation.
///
/// <para>
/// <b>SQLSTATE coverage (D-08 / ERROR-04 / ERROR-05):</b>
/// <c>23503</c> (foreign_key_violation) → 422 Unprocessable Entity;
/// <c>23505</c> (unique_violation) → 409 Conflict. Unmapped SqlStates return
/// <c>false</c> so the caller (<c>DbUpdateExceptionHandler</c>) falls through
/// to <c>FallbackExceptionHandler</c> 500 — the stack is still logged so we
/// discover unmapped SQLSTATEs naturally.
/// </para>
///
/// <para>
/// <b>Constraint-name convention (ERROR-11):</b> Phase 8 IEntityTypeConfiguration
/// files set FK constraint names as <c>fk_&lt;table&gt;_&lt;column&gt;</c>
/// (e.g., <c>fk_processor_input_schema_id</c>) and UQ names as
/// <c>uq_&lt;table&gt;_&lt;column&gt;</c> (e.g., <c>uq_processor_source_hash</c>).
/// </para>
///
/// <para>
/// <b>FK regex — Option A (preserves <c>_id</c> suffix):</b> per RESEARCH.md
/// "Constraint Name Regex Validation" recommendation, the regex captures the
/// full column name including <c>_id</c> so the response detail message
/// (<c>"input_schema_id"</c>) directly aligns with the snake_case ↔ camelCase
/// mapping of the DTO field (<c>inputSchemaId</c>). The D-08 original regex
/// stripped <c>_id</c>; this deviation is explicitly authorized by D-08
/// Claude's Discretion at CONTEXT.md line 193.
/// </para>
///
/// <para>
/// <b>Information-disclosure guard (T-04-FK / T-04-UQ):</b> the response detail
/// only carries the extracted column name — <c>pgEx.MessageText</c>,
/// <c>pgEx.Detail</c>, <c>pgEx.TableName</c>, <c>pgEx.SchemaName</c> NEVER appear
/// in the HTTP response body. Constraint names (public per ERROR-11) and column
/// names (part of the API DTO surface) are accepted risk.
/// </para>
/// </summary>
public static class PostgresExceptionMapper
{
    // ERROR-11 constraint name conventions per REQUIREMENTS.md.
    // Option A (preserves _id suffix) per RESEARCH lines 1059-1072 recommendation.
    private static readonly Regex FkRegex = new(
        @"^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$",
        RegexOptions.Compiled);

    private static readonly Regex UqRegex = new(
        @"^uq_[a-z0-9]+_(?<col>[a-z0-9_]+)$",
        RegexOptions.Compiled);

    public static bool TryMap(
        DbUpdateException ex,
        out int httpStatus,
        out string detail,
        out string? columnName)
    {
        httpStatus = StatusCodes.Status500InternalServerError;
        detail = string.Empty;
        columnName = null;

        if (ex.InnerException is not PostgresException pgEx) return false;

        switch (pgEx.SqlState)
        {
            case "23503":  // foreign_key_violation
                httpStatus = StatusCodes.Status422UnprocessableEntity;
                columnName = ExtractColumn(FkRegex, pgEx.ConstraintName);
                detail = columnName is not null
                    ? $"Foreign key violation: {columnName} references a non-existent record."
                    : "Foreign key constraint violated.";
                return true;

            case "23505":  // unique_violation
                httpStatus = StatusCodes.Status409Conflict;
                columnName = ExtractColumn(UqRegex, pgEx.ConstraintName);
                detail = columnName is not null
                    ? $"Unique constraint violation: {columnName} already exists."
                    : "Unique constraint violated.";
                return true;

            default:
                return false;  // unknown SQLSTATE → caller falls through to FallbackExceptionHandler
        }
    }

    private static string? ExtractColumn(Regex regex, string? constraintName)
    {
        if (string.IsNullOrEmpty(constraintName)) return null;
        var match = regex.Match(constraintName);
        return match.Success ? match.Groups["col"].Value : null;
    }
}
