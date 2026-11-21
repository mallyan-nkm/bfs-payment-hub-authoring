// durability is the worker of the durability check (README.md, "What is proved"). It serves one
// workflow, durability.v1: an activity, a timer, then a second activity. The check starts the
// workflow with the Temporal CLI, kills the server while the timer runs, starts it again, and shows
// that the workflow completes: its history and its timer were in SQL Server, not in the process.
//
//	go run . --address 127.0.0.1:7233 --task-queue durability
package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"
	"time"

	"go.temporal.io/sdk/activity"
	"go.temporal.io/sdk/client"
	"go.temporal.io/sdk/worker"
	"go.temporal.io/sdk/workflow"
)

// Workflow runs step 1, waits for the timer, runs step 2 and returns both results.
func Workflow(ctx workflow.Context, wait time.Duration) (string, error) {
	ctx = workflow.WithActivityOptions(ctx, workflow.ActivityOptions{StartToCloseTimeout: time.Minute})
	var first, second string
	if err := workflow.ExecuteActivity(ctx, Step, "before the restart").Get(ctx, &first); err != nil {
		return "", err
	}
	if err := workflow.Sleep(ctx, wait); err != nil {
		return "", err
	}
	if err := workflow.ExecuteActivity(ctx, Step, "after the restart").Get(ctx, &second); err != nil {
		return "", err
	}
	return first + "; " + second, nil
}

// Step reports which attempt ran it and when.
func Step(ctx context.Context, what string) (string, error) {
	info := activity.GetInfo(ctx)
	return fmt.Sprintf("%s ran at %s (attempt %d)", what, time.Now().UTC().Format(time.RFC3339), info.Attempt), nil
}

func main() {
	address := flag.String("address", "127.0.0.1:7233", "frontend host:port")
	namespace := flag.String("namespace", "default", "namespace")
	taskQueue := flag.String("task-queue", "durability", "task queue")
	flag.Parse()

	// Dial lazily, so the worker keeps retrying while the server is down.
	c, err := client.NewLazyClient(client.Options{HostPort: *address, Namespace: *namespace})
	if err != nil {
		log.Fatalf("client: %v", err)
	}
	defer c.Close()
	w := worker.New(c, *taskQueue, worker.Options{})
	w.RegisterWorkflowWithOptions(Workflow, workflow.RegisterOptions{Name: "durability.v1"})
	w.RegisterActivityWithOptions(Step, activity.RegisterOptions{Name: "durability.step"})
	if err := w.Start(); err != nil {
		log.Fatalf("worker: %v", err)
	}
	log.Printf("worker on %s, namespace %s, task queue %s", *address, *namespace, *taskQueue)
	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGINT, syscall.SIGTERM)
	<-stop
	w.Stop()
}
