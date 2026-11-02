export function formatTasks(tasks, includeDescriptions = false) {
  if (!tasks || tasks.length === 0) return "No tasks found.";

  let output = "Tasks:\n";
  for (const task of tasks) {
    output += `[${task.id.substring(0, 8)}] ${task.title} | ${task.status} | ${task.priority || "normal"}`;
    if (task.assignee) output += ` | ${task.assignee}`;
    output += "\n";

    if (includeDescriptions && task.description) {
      const description = task.description.length > 120
        ? task.description.substring(0, 120) + "..."
        : task.description;
      output += `   ${description}\n`;
    }
  }

  return output.trim();
}
