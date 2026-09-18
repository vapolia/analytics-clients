/**
 * Optional: hand the spool to the OS scheduler.
 *
 * This does **not** finish a send that was in flight when the app backgrounded — no JS API can, and
 * `expo-background-task` is explicit about it: the task runs when the system decides, at best every
 * 15 minutes, with battery and network to spare. What it buys is turning "the next launch" into
 * "some time later today" for a queue that is already on disk.
 *
 * It is opt-in because it is not free: it needs `expo-background-task` and `expo-task-manager`, plus
 * an `Info.plist` entry on iOS (handled by Continuous Native Generation), and therefore a new build.
 *
 * ```ts
 * // top level of the app, not inside a component
 * registerBackgroundFlush();
 * ```
 */

const TASK_NAME = 'vapolia.analytics.flush';

type Flusher = () => Promise<void>;

let flusher: Flusher | undefined;

/** Called by `start` so the registered task has something to flush. */
export function setBackgroundFlusher(flush: Flusher): void {
  flusher = flush;
}

export async function registerBackgroundFlush(): Promise<boolean> {
  if (typeof require !== 'function') return false;

  let taskManager: {
    defineTask(name: string, task: () => Promise<unknown>): void;
    isTaskRegisteredAsync(name: string): Promise<boolean>;
  };
  let backgroundTask: {
    registerTaskAsync(name: string, options?: { minimumInterval?: number }): Promise<void>;
    BackgroundTaskResult: { Success: unknown };
  };

  try {
    taskManager = require('expo-task-manager');
    backgroundTask = require('expo-background-task');
  } catch {
    return false;
  }

  try {
    taskManager.defineTask(TASK_NAME, async () => {
      await flusher?.();
      return backgroundTask.BackgroundTaskResult.Success;
    });

    if (await taskManager.isTaskRegisteredAsync(TASK_NAME)) return true;

    // 15 minutes is the floor the OS enforces anyway; asking for less changes nothing.
    await backgroundTask.registerTaskAsync(TASK_NAME, { minimumInterval: 15 });
    return true;
  } catch {
    return false;
  }
}
