using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaxyCandles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HashRefreshTokens : Migration
    {
        // Replaces the plaintext Token column with TokenHash, the SHA-256 of
        // the token (TokenService.HashRefreshToken). Existing rows are hashed
        // in place so current sessions survive: the C# side hashes the UTF-8
        // bytes of the token string, and the tokens are base64 (ASCII only),
        // so converting to varchar gives the same bytes under any collation.
        // Hashing the nvarchar directly would hash UTF-16 and match nothing.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_Token",
                table: "RefreshTokens");

            migrationBuilder.AddColumn<byte[]>(
                name: "TokenHash",
                table: "RefreshTokens",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE [RefreshTokens] " +
                "SET [TokenHash] = HASHBYTES('SHA2_256', CONVERT(varchar(450), [Token]));");

            migrationBuilder.AlterColumn<byte[]>(
                name: "TokenHash",
                table: "RefreshTokens",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "binary(32)",
                oldFixedLength: true,
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "Token",
                table: "RefreshTokens");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);
        }

        // A hash can't be turned back into the token, so rolling back deletes
        // every refresh token: everyone has to log in again.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens");

            migrationBuilder.Sql("DELETE FROM [RefreshTokens];");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "RefreshTokens");

            migrationBuilder.AddColumn<string>(
                name: "Token",
                table: "RefreshTokens",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_Token",
                table: "RefreshTokens",
                column: "Token",
                unique: true);
        }
    }
}
