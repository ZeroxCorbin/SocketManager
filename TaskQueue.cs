using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace SocketManagerNS
{
    public class TaskQueue
    {
        private readonly object _syncObj = new object();
        private readonly Queue<QTask> _tasks = new Queue<QTask>();
        private int _runningTaskCount;

        public void QueueTask(bool isParallel, Action task)
        {
            lock (_syncObj)
            {
                _tasks.Enqueue(new QTask { IsParallel = isParallel, Task = task });
            }

            ProcessTaskQueue();
        }

        public int TaskCount
        {
            get { lock (_syncObj) { return _tasks.Count; } }
        }

        private void ProcessTaskQueue()
        {
            lock (_syncObj)
            {
                if (_runningTaskCount != 0) return;

                while (_tasks.Count > 0 && _tasks.Peek().IsParallel)
                {
                    QTask parallelTask = _tasks.Dequeue();

                    QueueUserWorkItem(parallelTask);
                }

                if (_tasks.Count > 0 && _runningTaskCount == 0)
                {
                    QTask serialTask = _tasks.Dequeue();

                    QueueUserWorkItem(serialTask);
                }
            }
        }

        private void QueueUserWorkItem(QTask qTask)
        {
            Action completionTask = () =>
            {
                qTask.Task();

                OnTaskCompleted();
            };

            _runningTaskCount++;

            ThreadPool.QueueUserWorkItem(_ => completionTask());
        }

        private void OnTaskCompleted()
        {
            lock (_syncObj)
            {
                if (--_runningTaskCount == 0)
                {
                    ProcessTaskQueue();
                }
            }
        }

        private class QTask
        {
            public Action Task { get; set; }
            public bool IsParallel { get; set; }
        }
    }

    public class GroupedTaskQueue
    {
        private readonly object _syncObj = new object();
        private readonly Dictionary<string, TaskQueue> _queues = new Dictionary<string, TaskQueue>();
        private readonly string _defaultGroup = Guid.NewGuid().ToString();

        public void QueueTask(bool isParallel, Action task)
        {
            QueueTask(_defaultGroup, isParallel, task);
        }

        public void QueueTask(string group, bool isParallel, Action task)
        {
            TaskQueue queue;

            lock (_syncObj)
            {
                if (!_queues.TryGetValue(group, out queue))
                {
                    queue = new TaskQueue();

                    _queues.Add(group, queue);
                }
            }

            void completionTask()
            {
                task();

                OnTaskCompleted(group, queue);
            }

            queue.QueueTask(isParallel, completionTask);
        }

        private void OnTaskCompleted(string group, TaskQueue queue)
        {
            lock (_syncObj)
            {
                if (queue.TaskCount == 0)
                {
                    _queues.Remove(group);
                }
            }
        }
    }
}
