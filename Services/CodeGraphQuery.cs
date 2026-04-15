using System.Collections.Generic;
using System.Data;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Structured query methods for the C# code graph.
    /// Recursive CTEs for impact analysis, callers/callees, inheritance, dead code.
    /// Adapted from Frank's Clarion CodeGraph query layer.
    /// </summary>
    public class CodeGraphQuery
    {
        private readonly CodeGraphDatabase _db;

        public CodeGraphQuery(CodeGraphDatabase db)
        {
            _db = db;
        }

        /// <summary>
        /// Find symbols by name (exact match first, then substring).
        /// </summary>
        public DataTable FindSymbol(string searchText, string typeFilter = null)
        {
            var typeClause = string.IsNullOrEmpty(typeFilter)
                ? "AND s.type IN ('class','interface','struct','enum','method','property','constructor','delegate')"
                : "AND s.type = @typeFilter";

            string sqlExact = $@"
                SELECT s.id, s.name, s.type, s.file_path, s.line_number,
                       p.name AS project_name, s.params, s.return_type, s.scope,
                       s.accessibility, s.is_static, s.is_async, s.is_abstract,
                       s.generic_params, s.member_of
                FROM cg_symbols s
                LEFT JOIN cg_projects p ON s.project_id = p.id
                WHERE s.name = @exact COLLATE NOCASE {typeClause}
                ORDER BY s.name
                LIMIT 50";

            var parms = new Dictionary<string, object> { { "@exact", searchText } };
            if (!string.IsNullOrEmpty(typeFilter)) parms["@typeFilter"] = typeFilter;

            var result = _db.ExecuteQuery(sqlExact, parms);
            if (result.Rows.Count > 0)
                return result;

            string sqlLike = $@"
                SELECT s.id, s.name, s.type, s.file_path, s.line_number,
                       p.name AS project_name, s.params, s.return_type, s.scope,
                       s.accessibility, s.is_static, s.is_async, s.is_abstract,
                       s.generic_params, s.member_of
                FROM cg_symbols s
                LEFT JOIN cg_projects p ON s.project_id = p.id
                WHERE s.name LIKE @search COLLATE NOCASE {typeClause}
                ORDER BY
                  CASE WHEN s.name LIKE @prefix COLLATE NOCASE THEN 0 ELSE 1 END,
                  s.name
                LIMIT 50";

            parms["@search"] = "%" + searchText + "%";
            parms["@prefix"] = searchText + "%";
            return _db.ExecuteQuery(sqlLike, parms);
        }

        /// <summary>
        /// Who calls this method/property? (direct callers)
        /// </summary>
        public DataTable GetCallers(long symbolId)
        {
            const string sql = @"
                SELECT DISTINCT s.id, s.name, s.type, s.member_of,
                       r.file_path AS call_file, r.line_number AS call_line,
                       p.name AS project_name
                FROM cg_relationships r
                JOIN cg_symbols s ON r.from_id = s.id
                LEFT JOIN cg_projects p ON s.project_id = p.id
                WHERE r.to_id = @id AND r.type = 'calls'
                ORDER BY p.name, s.member_of, s.name";

            return _db.ExecuteQuery(sql, new Dictionary<string, object> { { "@id", symbolId } });
        }

        /// <summary>
        /// What does this method call? (direct callees)
        /// </summary>
        public DataTable GetCallees(long symbolId)
        {
            const string sql = @"
                SELECT DISTINCT s.id, s.name, s.type, s.member_of, s.file_path, s.line_number,
                       p.name AS project_name, r.line_number AS call_line, r.file_path AS call_file
                FROM cg_relationships r
                JOIN cg_symbols s ON r.to_id = s.id
                LEFT JOIN cg_projects p ON s.project_id = p.id
                WHERE r.from_id = @id AND r.type = 'calls'
                ORDER BY r.line_number";

            return _db.ExecuteQuery(sql, new Dictionary<string, object> { { "@id", symbolId } });
        }

        /// <summary>
        /// Impact analysis: transitive callers via recursive CTE with cycle detection.
        /// "If I change X, what's affected?"
        /// </summary>
        public DataTable GetImpact(long symbolId, int maxDepth = 10)
        {
            const string sql = @"
                WITH RECURSIVE impact(id, name, type, member_of, file_path, line_number, project_name, depth, path) AS (
                    SELECT s.id, s.name, s.type, s.member_of, s.file_path, s.line_number, p.name, 0, CAST(s.id AS TEXT)
                    FROM cg_symbols s
                    LEFT JOIN cg_projects p ON s.project_id = p.id
                    WHERE s.id = @id
                    UNION
                    SELECT s.id, s.name, s.type, s.member_of, s.file_path, s.line_number, p.name,
                           impact.depth + 1, impact.path || '>' || CAST(s.id AS TEXT)
                    FROM cg_relationships r
                    JOIN cg_symbols s ON r.from_id = s.id
                    LEFT JOIN cg_projects p ON s.project_id = p.id
                    JOIN impact ON r.to_id = impact.id
                    WHERE impact.depth < @maxDepth
                      AND ('>' || impact.path || '>') NOT LIKE ('%>' || CAST(s.id AS TEXT) || '>%')
                )
                SELECT DISTINCT id, name, type, member_of, file_path, line_number, project_name, depth
                FROM impact
                WHERE depth > 0
                ORDER BY depth, name";

            return _db.ExecuteQuery(sql, new Dictionary<string, object>
            {
                { "@id", symbolId },
                { "@maxDepth", maxDepth }
            });
        }

        /// <summary>
        /// Inheritance tree: who derives from this class/interface?
        /// </summary>
        public DataTable GetInheritanceTree(long classId)
        {
            const string sql = @"
                WITH RECURSIVE tree(id, name, type, file_path, line_number, parent_name, accessibility, depth) AS (
                    SELECT id, name, type, file_path, line_number, parent_name, accessibility, 0
                    FROM cg_symbols WHERE id = @id
                    UNION ALL
                    SELECT s.id, s.name, s.type, s.file_path, s.line_number, s.parent_name, s.accessibility, tree.depth + 1
                    FROM cg_symbols s
                    JOIN cg_relationships r ON r.from_id = s.id AND r.type IN ('inherits', 'implements')
                    JOIN tree ON r.to_id = tree.id
                    WHERE tree.depth < 10
                )
                SELECT * FROM tree ORDER BY depth";

            return _db.ExecuteQuery(sql, new Dictionary<string, object> { { "@id", classId } });
        }

        /// <summary>
        /// Dead code: public methods/properties never called from anywhere.
        /// </summary>
        public DataTable GetDeadCode(int? projectId = null)
        {
            var projectClause = projectId.HasValue ? "AND s.project_id = @projId" : "";
            string sql = $@"
                SELECT s.id, s.name, s.type, s.member_of, s.file_path, s.line_number,
                       s.accessibility, p.name AS project_name
                FROM cg_symbols s
                LEFT JOIN cg_projects p ON s.project_id = p.id
                LEFT JOIN cg_relationships r ON r.to_id = s.id AND r.type = 'calls'
                WHERE s.type IN ('method', 'property')
                  AND s.accessibility IN ('public', 'internal')
                  AND r.id IS NULL
                  {projectClause}
                ORDER BY p.name, s.member_of, s.name";

            var parms = new Dictionary<string, object>();
            if (projectId.HasValue) parms["@projId"] = projectId.Value;
            return _db.ExecuteQuery(sql, parms);
        }

        /// <summary>
        /// All symbols defined in a specific file.
        /// </summary>
        public DataTable GetFileSymbols(string filePath)
        {
            const string sql = @"
                SELECT s.id, s.name, s.type, s.line_number, s.params, s.return_type,
                       s.scope, s.accessibility, s.is_static, s.is_async, s.is_abstract,
                       s.member_of, s.generic_params, p.name AS project_name
                FROM cg_symbols s
                LEFT JOIN cg_projects p ON s.project_id = p.id
                WHERE s.file_path = @file
                ORDER BY s.line_number";

            return _db.ExecuteQuery(sql, new Dictionary<string, object> { { "@file", filePath } });
        }

        /// <summary>
        /// Index statistics.
        /// </summary>
        public DataTable GetStats()
        {
            const string sql = @"
                SELECT
                    (SELECT COUNT(*) FROM cg_projects) AS project_count,
                    (SELECT COUNT(DISTINCT file_path) FROM cg_symbols) AS file_count,
                    (SELECT COUNT(*) FROM cg_symbols WHERE type = 'class') AS class_count,
                    (SELECT COUNT(*) FROM cg_symbols WHERE type = 'interface') AS interface_count,
                    (SELECT COUNT(*) FROM cg_symbols WHERE type = 'method') AS method_count,
                    (SELECT COUNT(*) FROM cg_symbols WHERE type = 'property') AS property_count,
                    (SELECT COUNT(*) FROM cg_symbols WHERE type = 'constructor') AS constructor_count,
                    (SELECT COUNT(*) FROM cg_symbols WHERE type IN ('struct','enum','delegate')) AS other_type_count,
                    (SELECT COUNT(*) FROM cg_symbols) AS total_symbols,
                    (SELECT COUNT(*) FROM cg_relationships) AS relationship_count,
                    (SELECT value FROM cg_index_metadata WHERE key = 'last_indexed') AS last_indexed,
                    (SELECT value FROM cg_index_metadata WHERE key = 'index_duration_ms') AS duration_ms";

            return _db.ExecuteQuery(sql, null);
        }
    }
}
