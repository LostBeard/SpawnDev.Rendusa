---
description: How to do JavaScript interop using SpawnDev.BlazorJS
---

# SpawnDev.BlazorJS Interop Rules

## Core Principle
SpawnDev.BlazorJS provides **strongly-typed C# wrappers** for every JavaScript type. Always use these typed wrappers — never use raw `JSObject` with generic `Get<T>`/`Set`/`Call` patterns.

## Accessing Global JS Objects
Use `BlazorJSRuntime.JS` (available statically or via DI) to access global objects with their proper types:

```csharp
// CORRECT — use typed wrappers
using var navigator = JS.Get<Navigator>("navigator");
using var swContainer = navigator.ServiceWorker;

// OR access nested paths directly
using var swContainer = JS.Get<ServiceWorkerContainer>("navigator.serviceWorker");
```

```csharp
// WRONG — do NOT use generic JSObject
using var navigator = JS.Get<JSObject>("navigator");
var sw = navigator.JSRef!.Get<JSObject>("serviceWorker"); // NO!
```

## Common Typed Wrappers
All standard Web API types have wrappers in `SpawnDev.BlazorJS.JSObjects`:
- `Navigator`, `Window`, `Document`
- `ServiceWorkerContainer`, `ServiceWorkerRegistration`, `ServiceWorker`
- `MessagePort`, `MessageEvent`, `MessageChannel`
- `Blob`, `File`, `ArrayBuffer`, `Uint8Array`
- `FileSystemDirectoryHandle`, `FileSystemFileHandle`
- `URL`, `HTMLElement`, etc.

## JSRef — When NOT to Use
`JSRef` should **never** be needed for normal interop. If you find yourself using `JSRef`, the typed wrapper likely already has the method/property you need. If it genuinely doesn't, ask the user to add it to SpawnDev.BlazorJS rather than working around it.

## Passing Data to JS
Use plain C# DTOs or anonymous objects when posting messages or passing structured data. BlazorJS handles serialization:

```csharp
port.PostMessage(new { type = "vfs-meta", totalSize = 1024, contentType = "video/mp4" });
```

## Receiving Data from JS
Use `MessageEvent.GetData<T>()` with a DTO class to deserialize incoming messages:

```csharp
var data = messageEvent.GetData<MyMessageDto>();
```

## Events and ActionEvent
ActionEvent properties on JSObject wrappers (e.g. `port.OnMessage`, `element.OnClick`) handle `ActionCallback` creation and disposal automatically when you use delegates directly with `+=` and `-=`. **Prefer this approach** over explicitly creating `ActionCallback` instances:

```csharp
// PREFERRED — delegate directly, ActionCallback managed automatically
port.OnMessage += HandleMessage;
// later:
port.OnMessage -= HandleMessage;

async void HandleMessage(MessageEvent e) { /* ... */ }
```

```csharp
// ACCEPTABLE — only when you need manual lifecycle control (e.g. tracking in a CallbackGroup)
var cb = new ActionCallback<MessageEvent>(HandleMessage);
_callbacks.Add(cb);
port.OnMessage += cb;
// manual cleanup needed: port.OnMessage -= cb; cb.Dispose();
```

## IDisposable
All JS object wrappers implement `IDisposable`. Use `using` statements to prevent memory leaks:

```csharp
using var blob = await file.ReadRangeBlobAsync(offset, length);
using var arrayBuffer = await blob.ArrayBuffer();
```

