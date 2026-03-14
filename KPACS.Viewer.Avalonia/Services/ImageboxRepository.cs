using System.Globalization;
using Microsoft.Data.Sqlite;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class ImageboxRepository
{
    private readonly string _connectionString;

    public ImageboxRepository(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        string sql = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS studies (
                study_key INTEGER PRIMARY KEY AUTOINCREMENT,
                study_instance_uid TEXT NOT NULL UNIQUE,
                patient_name TEXT NOT NULL,
                patient_id TEXT NOT NULL,
                patient_birth_date TEXT NOT NULL DEFAULT '',
                accession_number TEXT NOT NULL,
                study_description TEXT NOT NULL,
                referring_physician TEXT NOT NULL,
                study_date TEXT NOT NULL,
                modalities TEXT NOT NULL,
                storage_path TEXT NOT NULL,
                source_path TEXT NOT NULL DEFAULT '',
                availability_status TEXT NOT NULL DEFAULT 'Imported',
                imported_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS series (
                series_key INTEGER PRIMARY KEY AUTOINCREMENT,
                study_key INTEGER NOT NULL,
                series_instance_uid TEXT NOT NULL UNIQUE,
                modality TEXT NOT NULL,
                body_part_examined TEXT NOT NULL DEFAULT '',
                series_description TEXT NOT NULL,
                series_number INTEGER NOT NULL,
                instance_count INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (study_key) REFERENCES studies(study_key) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS instances (
                instance_key INTEGER PRIMARY KEY AUTOINCREMENT,
                series_key INTEGER NOT NULL,
                sop_instance_uid TEXT NOT NULL UNIQUE,
                sop_class_uid TEXT NOT NULL DEFAULT '',
                file_path TEXT NOT NULL,
                source_file_path TEXT NOT NULL DEFAULT '',
                instance_number INTEGER NOT NULL,
                frame_count INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (series_key) REFERENCES series(series_key) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS volume_roi_anatomy_priors (
                prior_key INTEGER PRIMARY KEY AUTOINCREMENT,
                signature TEXT NOT NULL UNIQUE,
                anatomy_label TEXT NOT NULL,
                region_label TEXT NOT NULL,
                modality TEXT NOT NULL,
                body_part_examined TEXT NOT NULL DEFAULT '',
                study_description TEXT NOT NULL DEFAULT '',
                series_description TEXT NOT NULL DEFAULT '',
                normalized_center_x REAL NOT NULL,
                normalized_center_y REAL NOT NULL,
                normalized_center_z REAL NOT NULL,
                normalized_size_x REAL NOT NULL,
                normalized_size_y REAL NOT NULL,
                normalized_size_z REAL NOT NULL,
                estimated_volume_mm3 REAL NOT NULL DEFAULT 0,
                source_study_instance_uid TEXT NOT NULL DEFAULT '',
                source_series_instance_uid TEXT NOT NULL DEFAULT '',
                use_count INTEGER NOT NULL DEFAULT 1,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_studies_patient_name ON studies(patient_name);
            CREATE INDEX IF NOT EXISTS ix_studies_patient_id ON studies(patient_id);
            CREATE INDEX IF NOT EXISTS ix_studies_study_date ON studies(study_date);
            CREATE INDEX IF NOT EXISTS ix_series_study_key ON series(study_key);
            CREATE INDEX IF NOT EXISTS ix_instances_series_key ON instances(series_key);
            CREATE INDEX IF NOT EXISTS ix_volume_roi_priors_modality ON volume_roi_anatomy_priors(modality);
            CREATE INDEX IF NOT EXISTS ix_volume_roi_priors_body_part ON volume_roi_anatomy_priors(body_part_examined);
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureColumnExistsAsync(connection, "studies", "patient_birth_date", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync(connection, "studies", "source_path", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync(connection, "studies", "availability_status", "TEXT NOT NULL DEFAULT 'Imported'", cancellationToken);
        await EnsureColumnExistsAsync(connection, "series", "body_part_examined", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync(connection, "instances", "sop_class_uid", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnExistsAsync(connection, "instances", "source_file_path", "TEXT NOT NULL DEFAULT ''", cancellationToken);
    }

    public async Task<List<StudyListItem>> SearchStudiesAsync(StudyQuery query, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = BuildSearchSql(query);
        AddQueryParameters(command, query);

        var results = new List<StudyListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapStudyListItem(reader));
        }

        return results;
    }

    public async Task<StudyDetails?> GetStudyDetailsAsync(long studyKey, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        StudyListItem? study = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                  SELECT s.study_key, s.study_instance_uid, s.patient_name, s.patient_id, s.patient_birth_date, s.accession_number,
                       s.study_description, s.referring_physician, s.study_date, s.modalities,
                       s.storage_path, s.source_path, s.availability_status, s.imported_at_utc,
                       COALESCE((SELECT COUNT(*) FROM series sr WHERE sr.study_key = s.study_key), 0),
                       COALESCE((SELECT COUNT(*) FROM instances i JOIN series sr ON sr.series_key = i.series_key WHERE sr.study_key = s.study_key), 0)
                FROM studies s
                WHERE s.study_key = $studyKey
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$studyKey", studyKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                study = MapStudyListItem(reader);
            }
        }

        if (study is null)
        {
            return null;
        }

        var details = new StudyDetails { Study = study };
        var seriesLookup = new Dictionary<long, SeriesRecord>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT sr.series_key, sr.study_key, sr.series_instance_uid, sr.modality,
                      sr.body_part_examined, sr.series_description, sr.series_number, sr.instance_count,
                      i.instance_key, i.series_key, i.sop_instance_uid, i.sop_class_uid, i.file_path, i.source_file_path, i.instance_number, i.frame_count
                FROM series sr
                LEFT JOIN instances i ON i.series_key = sr.series_key
                WHERE sr.study_key = $studyKey
                ORDER BY sr.series_number, sr.series_description, i.instance_number, i.file_path;
                """;
            command.Parameters.AddWithValue("$studyKey", studyKey);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                long seriesKey = reader.GetInt64(0);
                if (!seriesLookup.TryGetValue(seriesKey, out SeriesRecord? series))
                {
                    series = new SeriesRecord
                    {
                        SeriesKey = seriesKey,
                        StudyKey = reader.GetInt64(1),
                        SeriesInstanceUid = reader.GetString(2),
                        Modality = reader.GetString(3),
                        BodyPart = reader.GetString(4),
                        SeriesDescription = reader.GetString(5),
                        SeriesNumber = reader.GetInt32(6),
                        InstanceCount = reader.GetInt32(7),
                    };
                    seriesLookup.Add(seriesKey, series);
                    details.Series.Add(series);
                }

                if (reader.IsDBNull(8))
                {
                    continue;
                }

                series.Instances.Add(new InstanceRecord
                {
                    InstanceKey = reader.GetInt64(8),
                    SeriesKey = reader.GetInt64(9),
                    SopInstanceUid = reader.GetString(10),
                    SopClassUid = reader.GetString(11),
                    FilePath = reader.GetString(12),
                    SourceFilePath = reader.GetString(13),
                    InstanceNumber = reader.GetInt32(14),
                    FrameCount = reader.GetInt32(15),
                });
            }
        }

        details.PopulateLegacyStudyInfo();
        return details;
    }

    public async Task<StudyDetails?> GetStudyDetailsByStudyInstanceUidAsync(string studyInstanceUid, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT study_key FROM studies WHERE study_instance_uid = $studyInstanceUid LIMIT 1;";
        command.Parameters.AddWithValue("$studyInstanceUid", studyInstanceUid);

        object? scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
        {
            return null;
        }

        return await GetStudyDetailsAsync((long)scalar, cancellationToken);
    }

    public async Task<List<StudyListItem>> FindPriorStudiesAsync(StudyListItem study, int maxResults = 12, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(study);

        if (string.IsNullOrWhiteSpace(study.PatientId) && string.IsNullOrWhiteSpace(study.PatientName))
        {
            return [];
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var whereParts = new List<string>
        {
            "s.study_instance_uid <> $studyInstanceUid",
        };

        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$studyInstanceUid", study.StudyInstanceUid);

        if (!string.IsNullOrWhiteSpace(study.PatientId))
        {
            whereParts.Add("s.patient_id = $patientId");
            command.Parameters.AddWithValue("$patientId", study.PatientId.Trim());
        }
        else
        {
            whereParts.Add("s.patient_name = $patientName");
            command.Parameters.AddWithValue("$patientName", study.PatientName.Trim());

            if (!string.IsNullOrWhiteSpace(study.PatientBirthDate))
            {
                whereParts.Add("s.patient_birth_date = $patientBirthDate");
                command.Parameters.AddWithValue("$patientBirthDate", study.PatientBirthDate.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(study.StudyDate) && study.StudyDate.Length >= 8)
        {
            whereParts.Add("s.study_date < $studyDate");
            command.Parameters.AddWithValue("$studyDate", study.StudyDate[..8]);
        }

        command.Parameters.AddWithValue("$maxResults", Math.Max(1, maxResults));
        command.CommandText = $"""
            SELECT s.study_key, s.study_instance_uid, s.patient_name, s.patient_id, s.patient_birth_date, s.accession_number,
                   s.study_description, s.referring_physician, s.study_date, s.modalities,
                   s.storage_path, s.source_path, s.availability_status, s.imported_at_utc,
                   COALESCE((SELECT COUNT(*) FROM series sr WHERE sr.study_key = s.study_key), 0) AS series_count,
                   COALESCE((SELECT COUNT(*) FROM instances i JOIN series sr ON sr.series_key = i.series_key WHERE sr.study_key = s.study_key), 0) AS instance_count
            FROM studies s
            WHERE {string.Join(" AND ", whereParts)}
            ORDER BY s.study_date DESC, s.imported_at_utc DESC, s.patient_name ASC
            LIMIT $maxResults;
            """;

        var results = new List<StudyListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapStudyListItem(reader));
        }

        return results;
    }

    public async Task<long> UpsertStudyAsync(StudyListItem study, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO studies(study_instance_uid, patient_name, patient_id, patient_birth_date, accession_number, study_description,
                                referring_physician, study_date, modalities, storage_path, source_path, availability_status, imported_at_utc, updated_at_utc)
            VALUES ($uid, $patientName, $patientId, $patientBirthDate, $accessionNumber, $studyDescription, $refPhys, $studyDate,
                    $modalities, $storagePath, $sourcePath, $availabilityStatus, $importedAtUtc, $updatedAtUtc)
            ON CONFLICT(study_instance_uid) DO UPDATE SET
                patient_name = excluded.patient_name,
                patient_id = excluded.patient_id,
                patient_birth_date = excluded.patient_birth_date,
                accession_number = excluded.accession_number,
                study_description = excluded.study_description,
                referring_physician = excluded.referring_physician,
                study_date = excluded.study_date,
                modalities = excluded.modalities,
                storage_path = excluded.storage_path,
                source_path = excluded.source_path,
                availability_status = excluded.availability_status,
                updated_at_utc = excluded.updated_at_utc
            RETURNING study_key;
            """;
        FillStudyParameters(command, study);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task<long> UpsertSeriesAsync(long studyKey, SeriesRecord series, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO series(study_key, series_instance_uid, modality, body_part_examined, series_description, series_number, instance_count)
            VALUES ($studyKey, $uid, $modality, $bodyPartExamined, $seriesDescription, $seriesNumber, $instanceCount)
            ON CONFLICT(series_instance_uid) DO UPDATE SET
                study_key = excluded.study_key,
                modality = excluded.modality,
                body_part_examined = excluded.body_part_examined,
                series_description = excluded.series_description,
                series_number = excluded.series_number,
                instance_count = excluded.instance_count
            RETURNING series_key;
            """;
        command.Parameters.AddWithValue("$studyKey", studyKey);
        command.Parameters.AddWithValue("$uid", series.SeriesInstanceUid);
        command.Parameters.AddWithValue("$modality", series.Modality);
        command.Parameters.AddWithValue("$bodyPartExamined", series.BodyPart);
        command.Parameters.AddWithValue("$seriesDescription", series.SeriesDescription);
        command.Parameters.AddWithValue("$seriesNumber", series.SeriesNumber);
        command.Parameters.AddWithValue("$instanceCount", series.InstanceCount);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task UpsertInstanceAsync(long seriesKey, InstanceRecord instance, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO instances(series_key, sop_instance_uid, sop_class_uid, file_path, source_file_path, instance_number, frame_count)
            VALUES ($seriesKey, $uid, $sopClassUid, $filePath, $sourceFilePath, $instanceNumber, $frameCount)
            ON CONFLICT(sop_instance_uid) DO UPDATE SET
                series_key = excluded.series_key,
                sop_class_uid = excluded.sop_class_uid,
                file_path = excluded.file_path,
                source_file_path = excluded.source_file_path,
                instance_number = excluded.instance_number,
                frame_count = excluded.frame_count;
            """;
        command.Parameters.AddWithValue("$seriesKey", seriesKey);
        command.Parameters.AddWithValue("$uid", instance.SopInstanceUid);
        command.Parameters.AddWithValue("$sopClassUid", instance.SopClassUid);
        command.Parameters.AddWithValue("$filePath", instance.FilePath);
        command.Parameters.AddWithValue("$sourceFilePath", string.IsNullOrWhiteSpace(instance.SourceFilePath) ? instance.FilePath : instance.SourceFilePath);
        command.Parameters.AddWithValue("$instanceNumber", instance.InstanceNumber);
        command.Parameters.AddWithValue("$frameCount", instance.FrameCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertVolumeRoiAnatomyPriorAsync(VolumeRoiAnatomyPriorRecord prior, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO volume_roi_anatomy_priors(
                signature, anatomy_label, region_label, modality, body_part_examined, study_description, series_description,
                normalized_center_x, normalized_center_y, normalized_center_z,
                normalized_size_x, normalized_size_y, normalized_size_z,
                estimated_volume_mm3, source_study_instance_uid, source_series_instance_uid,
                use_count, created_at_utc, updated_at_utc)
            VALUES (
                $signature, $anatomyLabel, $regionLabel, $modality, $bodyPartExamined, $studyDescription, $seriesDescription,
                $centerX, $centerY, $centerZ,
                $sizeX, $sizeY, $sizeZ,
                $estimatedVolumeMm3, $sourceStudyInstanceUid, $sourceSeriesInstanceUid,
                $useCount, $createdAtUtc, $updatedAtUtc)
            ON CONFLICT(signature) DO UPDATE SET
                anatomy_label = excluded.anatomy_label,
                region_label = excluded.region_label,
                modality = excluded.modality,
                body_part_examined = excluded.body_part_examined,
                study_description = excluded.study_description,
                series_description = excluded.series_description,
                normalized_center_x = excluded.normalized_center_x,
                normalized_center_y = excluded.normalized_center_y,
                normalized_center_z = excluded.normalized_center_z,
                normalized_size_x = excluded.normalized_size_x,
                normalized_size_y = excluded.normalized_size_y,
                normalized_size_z = excluded.normalized_size_z,
                estimated_volume_mm3 = excluded.estimated_volume_mm3,
                source_study_instance_uid = excluded.source_study_instance_uid,
                source_series_instance_uid = excluded.source_series_instance_uid,
                use_count = volume_roi_anatomy_priors.use_count + 1,
                updated_at_utc = excluded.updated_at_utc;
            """;
        FillVolumeRoiPriorParameters(command, prior);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<VolumeRoiAnatomyPriorRecord>> GetVolumeRoiAnatomyPriorsAsync(string? modality, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(modality)
            ? """
                SELECT prior_key, signature, anatomy_label, region_label, modality, body_part_examined, study_description, series_description,
                       normalized_center_x, normalized_center_y, normalized_center_z,
                       normalized_size_x, normalized_size_y, normalized_size_z,
                       estimated_volume_mm3, source_study_instance_uid, source_series_instance_uid,
                       updated_at_utc, use_count
                FROM volume_roi_anatomy_priors
                ORDER BY updated_at_utc DESC
                LIMIT 500;
                """
            : """
                SELECT prior_key, signature, anatomy_label, region_label, modality, body_part_examined, study_description, series_description,
                       normalized_center_x, normalized_center_y, normalized_center_z,
                       normalized_size_x, normalized_size_y, normalized_size_z,
                       estimated_volume_mm3, source_study_instance_uid, source_series_instance_uid,
                       updated_at_utc, use_count
                FROM volume_roi_anatomy_priors
                WHERE modality = $modality OR modality = ''
                ORDER BY updated_at_utc DESC
                LIMIT 500;
                """;

        if (!string.IsNullOrWhiteSpace(modality))
        {
            command.Parameters.AddWithValue("$modality", modality.Trim());
        }

        var results = new List<VolumeRoiAnatomyPriorRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new VolumeRoiAnatomyPriorRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetDouble(8),
                reader.GetDouble(9),
                reader.GetDouble(10),
                reader.GetDouble(11),
                reader.GetDouble(12),
                reader.GetDouble(13),
                reader.GetDouble(14),
                reader.GetString(15),
                reader.GetString(16),
                DateTime.TryParse(reader.GetString(17), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime updatedAtUtc) ? updatedAtUtc : DateTime.UtcNow,
                reader.GetInt32(18)));
        }

        return results;
    }

    public async Task UpdateStudyImportStateAsync(long studyKey, StudyAvailability availability, string storagePath, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE studies
            SET availability_status = $availabilityStatus,
                storage_path = $storagePath,
                updated_at_utc = $updatedAtUtc
            WHERE study_key = $studyKey;
            """;
        command.Parameters.AddWithValue("$studyKey", studyKey);
        command.Parameters.AddWithValue("$availabilityStatus", availability.ToString());
        command.Parameters.AddWithValue("$storagePath", storagePath);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteInstanceBySopInstanceUidAsync(string sopInstanceUid, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM instances WHERE sop_instance_uid = $sopInstanceUid;";
        command.Parameters.AddWithValue("$sopInstanceUid", sopInstanceUid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteSeriesIfEmptyAsync(string seriesInstanceUid, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM series
            WHERE series_instance_uid = $seriesInstanceUid
              AND NOT EXISTS (
                  SELECT 1
                  FROM instances i
                  WHERE i.series_key = series.series_key
              );
            """;
        command.Parameters.AddWithValue("$seriesInstanceUid", seriesInstanceUid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateStudyAfterPseudonymizeAsync(long studyKey, PseudonymizeRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE studies
            SET patient_name = $patientName,
                patient_id = $patientId,
                patient_birth_date = $patientBirthDate,
                accession_number = $accessionNumber,
                referring_physician = $refPhys,
                updated_at_utc = $updatedAtUtc
            WHERE study_key = $studyKey;
            """;
        command.Parameters.AddWithValue("$studyKey", studyKey);
        command.Parameters.AddWithValue("$patientName", request.PatientName ?? string.Empty);
        command.Parameters.AddWithValue("$patientId", request.PatientId ?? string.Empty);
        command.Parameters.AddWithValue("$patientBirthDate", request.PatientBirthDate ?? string.Empty);
        command.Parameters.AddWithValue("$accessionNumber", request.AccessionNumber ?? string.Empty);
        command.Parameters.AddWithValue("$refPhys", request.ReferringPhysician ?? string.Empty);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteStudyAsync(long studyKey, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM studies WHERE study_key = $studyKey;";
        command.Parameters.AddWithValue("$studyKey", studyKey);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnExistsAsync(SqliteConnection connection, string tableName, string columnName, string columnSql, CancellationToken cancellationToken)
    {
        await using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = $"PRAGMA table_info({tableName});";

        bool exists = false;
        await using (var reader = await pragmaCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnSql};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildSearchSql(StudyQuery query)
    {
        var whereParts = new List<string> { "1 = 1" };
        if (!string.IsNullOrWhiteSpace(query.PatientId)) whereParts.Add("s.patient_id LIKE $patientId");
        if (!string.IsNullOrWhiteSpace(query.PatientName)) whereParts.Add("s.patient_name LIKE $patientName");
        if (!string.IsNullOrWhiteSpace(query.PatientBirthDate)) whereParts.Add("s.patient_birth_date = $patientBirthDate");
        if (!string.IsNullOrWhiteSpace(query.AccessionNumber)) whereParts.Add("s.accession_number LIKE $accessionNumber");
        if (!string.IsNullOrWhiteSpace(query.ReferringPhysician)) whereParts.Add("s.referring_physician LIKE $refPhysician");
        if (!string.IsNullOrWhiteSpace(query.StudyDescription)) whereParts.Add("s.study_description LIKE $studyDescription");
        if (!string.IsNullOrWhiteSpace(query.QuickSearch))
        {
            whereParts.Add("(s.patient_name LIKE $quickSearch OR s.patient_id LIKE $quickSearch OR s.study_description LIKE $quickSearch OR s.modalities LIKE $quickSearch)");
        }
        if (query.Modalities.Count > 0)
        {
            whereParts.Add("(" + string.Join(" OR ", query.Modalities.Select((_, index) => $"s.modalities LIKE $modality{index}")) + ")");
        }
        if (query.FromStudyDate is not null) whereParts.Add("s.study_date >= $fromStudyDate");
        if (query.ToStudyDate is not null) whereParts.Add("s.study_date <= $toStudyDate");

        return $"""
            SELECT s.study_key, s.study_instance_uid, s.patient_name, s.patient_id, s.patient_birth_date, s.accession_number,
                   s.study_description, s.referring_physician, s.study_date, s.modalities,
                   s.storage_path, s.source_path, s.availability_status, s.imported_at_utc,
                   COALESCE((SELECT COUNT(*) FROM series sr WHERE sr.study_key = s.study_key), 0) AS series_count,
                   COALESCE((SELECT COUNT(*) FROM instances i JOIN series sr ON sr.series_key = i.series_key WHERE sr.study_key = s.study_key), 0) AS instance_count
            FROM studies s
            WHERE {string.Join(" AND ", whereParts)}
            ORDER BY s.study_date DESC, s.imported_at_utc DESC, s.patient_name ASC;
            """;
    }

    private static DateTime ParseImportedAtUtc(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed))
        {
            return parsed;
        }

        return DateTime.UtcNow;
    }

    private static StudyAvailability ParseStudyAvailability(string value)
    {
        if (Enum.TryParse(value, true, out StudyAvailability parsed))
        {
            return parsed;
        }

        return StudyAvailability.Imported;
    }

    private static void AddQueryParameters(SqliteCommand command, StudyQuery query)
    {
        command.Parameters.AddWithValue("$patientId", Like(query.PatientId));
        command.Parameters.AddWithValue("$patientName", Like(query.PatientName));
        command.Parameters.AddWithValue("$patientBirthDate", query.PatientBirthDate.Trim());
        command.Parameters.AddWithValue("$accessionNumber", Like(query.AccessionNumber));
        command.Parameters.AddWithValue("$refPhysician", Like(query.ReferringPhysician));
        command.Parameters.AddWithValue("$studyDescription", Like(query.StudyDescription));
        command.Parameters.AddWithValue("$quickSearch", Like(query.QuickSearch));
        for (int index = 0; index < query.Modalities.Count; index++)
        {
            command.Parameters.AddWithValue($"$modality{index}", Like(query.Modalities[index]));
        }
        command.Parameters.AddWithValue("$fromStudyDate", query.FromStudyDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("$toStudyDate", query.ToStudyDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "99999999");
    }

    private static string Like(string value) => $"%{value?.Trim() ?? string.Empty}%";

    private static void FillStudyParameters(SqliteCommand command, StudyListItem study)
    {
        command.Parameters.AddWithValue("$uid", study.StudyInstanceUid);
        command.Parameters.AddWithValue("$patientName", study.PatientName);
        command.Parameters.AddWithValue("$patientId", study.PatientId);
        command.Parameters.AddWithValue("$patientBirthDate", study.PatientBirthDate);
        command.Parameters.AddWithValue("$accessionNumber", study.AccessionNumber);
        command.Parameters.AddWithValue("$studyDescription", study.StudyDescription);
        command.Parameters.AddWithValue("$refPhys", study.ReferringPhysician);
        command.Parameters.AddWithValue("$studyDate", study.StudyDate);
        command.Parameters.AddWithValue("$modalities", study.Modalities);
        command.Parameters.AddWithValue("$storagePath", study.StoragePath);
        command.Parameters.AddWithValue("$sourcePath", study.SourcePath);
        command.Parameters.AddWithValue("$availabilityStatus", study.Availability.ToString());
        command.Parameters.AddWithValue("$importedAtUtc", study.ImportedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void FillVolumeRoiPriorParameters(SqliteCommand command, VolumeRoiAnatomyPriorRecord prior)
    {
        string timestamp = (prior.UpdatedAtUtc == default ? DateTime.UtcNow : prior.UpdatedAtUtc)
            .ToString("O", CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$signature", prior.Signature);
        command.Parameters.AddWithValue("$anatomyLabel", prior.AnatomyLabel);
        command.Parameters.AddWithValue("$regionLabel", prior.RegionLabel);
        command.Parameters.AddWithValue("$modality", prior.Modality);
        command.Parameters.AddWithValue("$bodyPartExamined", prior.BodyPartExamined);
        command.Parameters.AddWithValue("$studyDescription", prior.StudyDescription);
        command.Parameters.AddWithValue("$seriesDescription", prior.SeriesDescription);
        command.Parameters.AddWithValue("$centerX", prior.NormalizedCenterX);
        command.Parameters.AddWithValue("$centerY", prior.NormalizedCenterY);
        command.Parameters.AddWithValue("$centerZ", prior.NormalizedCenterZ);
        command.Parameters.AddWithValue("$sizeX", prior.NormalizedSizeX);
        command.Parameters.AddWithValue("$sizeY", prior.NormalizedSizeY);
        command.Parameters.AddWithValue("$sizeZ", prior.NormalizedSizeZ);
        command.Parameters.AddWithValue("$estimatedVolumeMm3", prior.EstimatedVolumeCubicMillimeters);
        command.Parameters.AddWithValue("$sourceStudyInstanceUid", prior.SourceStudyInstanceUid);
        command.Parameters.AddWithValue("$sourceSeriesInstanceUid", prior.SourceSeriesInstanceUid);
        command.Parameters.AddWithValue("$useCount", Math.Max(1, prior.UseCount));
        command.Parameters.AddWithValue("$createdAtUtc", timestamp);
        command.Parameters.AddWithValue("$updatedAtUtc", timestamp);
    }

    private static StudyListItem MapStudyListItem(SqliteDataReader reader)
    {
        var study = new StudyListItem
        {
            StudyKey = reader.GetInt64(0),
            StudyInstanceUid = reader.GetString(1),
            PatientName = reader.GetString(2),
            PatientId = reader.GetString(3),
            PatientBirthDate = reader.GetString(4),
            AccessionNumber = reader.GetString(5),
            StudyDescription = reader.GetString(6),
            ReferringPhysician = reader.GetString(7),
            StudyDate = reader.GetString(8),
            Modalities = reader.GetString(9),
            StoragePath = reader.GetString(10),
            SourcePath = reader.GetString(11),
            Availability = ParseStudyAvailability(reader.GetString(12)),
            ImportedAtUtc = ParseImportedAtUtc(reader.GetString(13)),
            SeriesCount = reader.GetInt32(14),
            InstanceCount = reader.GetInt32(15),
        };

        study.IsPreviewOnly = study.Availability != StudyAvailability.Imported;
        study.PreviewSourcePath = study.SourcePath;
        return study;
    }
}
