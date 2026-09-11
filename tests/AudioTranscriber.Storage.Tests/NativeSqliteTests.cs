using Microsoft.Data.Sqlite;
using Xunit;

namespace AudioTranscriber.Storage.Tests;

public sealed class NativeSqliteTests
{
    [Fact]
    public void NativeBuildSupportsGroupedQueriesAndFullTextSearch()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Assert.True(Version.Parse(connection.ServerVersion) >= new Version(3, 53, 4));

        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE jobs(state TEXT, session_id TEXT)";
        command.ExecuteNonQuery();

        // A zero-filled native code page previously crashed this prepare, even on an empty table.
        command.CommandText = "SELECT state FROM jobs GROUP BY state";
        using (var reader = command.ExecuteReader())
            Assert.False(reader.Read());

        command.CommandText = """
            INSERT INTO jobs VALUES('Succeeded','session'),('Succeeded','session');
            CREATE VIRTUAL TABLE transcript_fts USING fts5(text);
            INSERT INTO transcript_fts(text) VALUES('the dragon'),('the dragon');
            UPDATE transcript_fts SET text='the dungeon' WHERE rowid=1;
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT state,count(*) AS count FROM jobs WHERE session_id=$session GROUP BY state";
        command.Parameters.AddWithValue("$session", "session");
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal("Succeeded", reader.GetString(0));
            Assert.Equal(2, reader.GetInt64(1));
            Assert.False(reader.Read());
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT rowid FROM transcript_fts WHERE transcript_fts MATCH $search";
        command.Parameters.AddWithValue("$search", "dungeon");
        Assert.Equal(1L, command.ExecuteScalar());
        command.Parameters["$search"].Value = "dragon";
        Assert.Equal(2L, command.ExecuteScalar());
    }
}
