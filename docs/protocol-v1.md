# Network HID Bridge Protocol v1

## Scope and transport

Protocol v1 carries HID state over one ordered TCP byte stream. TCP endpoints MUST set `TCP_NODELAY`. The protocol provides peer authentication, frame integrity, and replay/reordering detection. It provides no encryption or confidentiality; headers, HID payloads, and timing are visible to an observer.

All multi-byte integers are unsigned big-endian unless a field is explicitly marked signed. Signed integers use two's-complement big-endian representation. Byte counts in this document are exact.

## Frame header

Every frame starts with this 20-byte header:

| Offset | Size | Field | Required value |
| ---: | ---: | --- | --- |
| 0 | 4 | magic | ASCII `NHID` (`4e484944`) |
| 4 | 1 | version | `1` |
| 5 | 1 | message type | One value from the message table below |
| 6 | 2 | flags | `0` |
| 8 | 8 | sequence | Handshake: `0`; authenticated traffic: directional sequence described below |
| 16 | 4 | payload length | Number of payload bytes, from `0` through `4096` inclusive |

The payload immediately follows the header. A handshake frame ends after its payload. An authenticated frame has a 16-byte authentication tag immediately after its payload. Payload length excludes the header and tag.

A receiver MUST validate magic, version, flags, known message type, phase-appropriate message type, declared payload length, and the message's exact payload length. It MUST reject a declared payload length greater than 4096 before allocating or reading that payload. End of stream before a complete header, declared payload, or required tag is a truncated-frame error.

## Message types and payloads

| Type | Name | Phase/direction | Exact payload |
| ---: | --- | --- | --- |
| `0x01` | ClientHello | Handshake, client to server | `client_nonce`, 32 bytes |
| `0x02` | ServerHello | Handshake, server to client | `server_nonce`, 32 bytes; then `server_proof`, 32 bytes |
| `0x03` | ClientAuth | Handshake, client to server | `client_proof`, 32 bytes |
| `0x10` | KeyboardState | Authenticated, client to server | One 8-byte USB boot-keyboard input report: modifier byte, reserved byte, six usage bytes |
| `0x11` | MouseState | Authenticated, client to server | `buttons:u8`, `dx:i32`, `dy:i32`, `wheel:i16`, `pan:i16`, in that order; 13 bytes total |
| `0x12` | ConsumerState | Authenticated, client to server | `usage:u16`; 2 bytes; zero means released |
| `0x13` | FullState | Authenticated, client to server | keyboard report (8 bytes), `mouse_buttons:u8`, `consumer_usage:u16`; 11 bytes total |
| `0x14` | ReleaseAll | Authenticated, client to server | Empty; 0 bytes |
| `0x20` | Heartbeat | Authenticated, client to server | Client monotonic timestamp `u64`; 8 bytes |
| `0x21` | HeartbeatAck | Authenticated, server to client | The exact timestamp from the corresponding Heartbeat as `u64`; 8 bytes |

`dx`, `dy`, `wheel`, and `pan` are accumulated relative motion, not absolute positions. `FullState.mouse_buttons` is a button snapshot and carries no relative movement. A receiver MUST reject a known type whose payload length differs from the exact size in this table, even when the length is below 4096.

No message types other than those in this table are defined in v1. Handshake types are invalid after authentication, and authenticated types are invalid before authentication.

## Handshake and key derivation

The PSK is a configured byte string shared by exactly the Windows client and Pi receiver. It is not transmitted. Nonces MUST be independently generated, unpredictable 32-byte values for every connection and MUST NOT be reused intentionally.

The only valid handshake transcript is:

1. Client sends `ClientHello` with sequence `0`, no tag, and a fresh `client_nonce`.
2. Server sends `ServerHello` with sequence `0`, no tag, a fresh `server_nonce`, and `server_proof`.
3. Client verifies `server_proof`. On success it sends `ClientAuth` with sequence `0`, no tag, and `client_proof`.
4. Server verifies `client_proof`. Both peers derive the same `session_key`. Authenticated traffic may then begin.

The byte formulas are exact. Quoted labels below are their lowercase ASCII bytes, with no NUL, length prefix, separator, or newline:

```text
server_proof = HMAC-SHA256(PSK, ASCII("server") || client_nonce || server_nonce)
client_proof = HMAC-SHA256(PSK, ASCII("client") || client_nonce || server_nonce)
session_key  = HMAC-SHA256(PSK, ASCII("session") || client_nonce || server_nonce)
```

`||` means byte concatenation. The nonce order is always client nonce followed by server nonce. Proof comparisons MUST be constant-time. A bad proof, wrong handshake order, wrong handshake payload size, nonzero handshake sequence, or tagged handshake frame terminates the connection. No HID message may be acted upon before the handshake completes.

## Authenticated frames and sequencing

After authentication, client-to-server and server-to-client are independent sequence spaces. Each sender's first authenticated frame MUST use sequence `1`; each following authenticated frame in that direction MUST use exactly the prior sequence plus one. Sequence `0`, a repeated value, and a skipped or out-of-order value are errors. Sequence state is reset only by completing a new handshake on a new connection. Sequence exhaustion at `2^64 - 1` requires closing the connection; wrapping to zero is forbidden.

For every authenticated frame, compute:

```text
full_tag = HMAC-SHA256(session_key, header || payload)
wire_tag = full_tag[0:16]
wire_frame = header || payload || wire_tag
```

The tag covers the exact 20 header bytes as transmitted, including magic, version, type, flags, sequence, and payload length, followed by the exact payload bytes. Truncation keeps the first 16 bytes of the 32-byte HMAC output. The tag is not included in its own HMAC input. Receivers MUST compare all 16 tag bytes in constant time and MUST reject a wrong or missing tag before dispatching the message. A correctly tagged frame with an unexpected sequence is still rejected.

## Scheduling and state synchronization

The client sends KeyboardState and ConsumerState immediately whenever their state changes. While remote routing is active, it also sends FullState every 500 milliseconds, measured from a monotonic clock. FullState repairs lost application state but does not relax sequence validation.

The client accumulates mouse movement and wheel/pan deltas, then sends MouseState at no more than 125 frames per second (a minimum interval of 8 milliseconds). Button changes are reflected in the next MouseState. Coalescing MUST preserve accumulated signed deltas; the Pi may split them into multiple HID-sized reports.

The client sends Heartbeat every 250 milliseconds using a monotonic timestamp. The Pi responds with HeartbeatAck carrying the identical 64-bit value. Heartbeats and acknowledgements are ordinary authenticated, sequenced frames. Any valid authenticated frame refreshes the Pi's receive watchdog; invalid or incomplete data does not.

## Ownership, errors, and release behavior

At most one authenticated client owns HID output. While an authenticated session owns it, the Pi MUST NOT complete a handshake for another connection; it closes or refuses the additional connection without transferring ownership. A new client can authenticate only after the owner has closed, failed, or timed out and release processing has completed. A new session starts with empty keyboard, mouse-button, and consumer state.

Any of the following is a fatal protocol error: bad magic, unsupported version, nonzero flags, unknown or phase-inappropriate type, payload over 4096 bytes, type-specific length mismatch, truncated frame, unexpected tag presence, wrong or missing tag, bad handshake proof, replayed sequence, or skipped/out-of-order sequence. A fatal protocol error terminates that TCP connection; the receiver does not attempt stream resynchronization.

The Pi MUST write empty keyboard, mouse, and consumer reports before relinquishing an owning session when:

- TCP closes or resets;
- any fatal protocol or authentication error occurs after the session could have affected HID state;
- no complete valid frame is received for 1 second;
- the receiver session is cancelled or the receiver shuts down; or
- HID output itself fails, to the extent the device nodes remain writable.

ReleaseAll causes the same three empty HID reports immediately but does not itself close a valid session. When Windows disables remote routing, it sends ReleaseAll before discarding remote state. On Windows socket failure, routing immediately returns to Default/local mode. On reconnect, controls already physically held are not sent remotely until released and pressed again.

Implementations MUST NOT log the PSK, nonces, proofs, session key, tags, payloads, key usages, button state, or movement values. Lifecycle and error categories may be logged.

## Interoperability fixtures

[`tests/fixtures/protocol-v1.json`](../tests/fixtures/protocol-v1.json) contains immutable lowercase-hex examples generated once from fixed, non-secret test inputs. It includes all headers and payload layouts, the complete handshake transcript and derived values, one valid authenticated frame, and isolated invalid-tag, skipped-sequence, and oversized-length cases. Those literal expected values are the cross-language oracle; production implementations MUST NOT recalculate test expectations with the same code under test.
