import Database from 'better-sqlite3';
import path from 'path';

const appData = process.env.APPDATA;
const dbPath = path.join(appData, 'multiterminal', 'sessions.db');

try {
  const db = new Database(dbPath);
  db.pragma('journal_mode = WAL');
  
  const sessions = db.prepare(`
    SELECT 
      id, 
      title, 
      project_path,
      created_at,
      last_activity_at,
      message_count,
      summary
    FROM sessions
    WHERE project_path LIKE '%MultiTerminal%'
    ORDER BY created_at DESC
    LIMIT 20
  `).all();
  
  console.log(JSON.stringify(sessions, null, 2));
  
  db.close();
} catch (err) {
  console.error('Error:', err.message);
  process.exit(1);
}
