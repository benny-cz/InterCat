# Remote agent contract, version 1

Status: M11, revision 1093 (ADR-117): enrollment, the pinned transport, hello and inventory
Owner: `InterCat.Remote` (`RemoteAgentServer`, `AgentEnrollments`, `RemoteAgentConnection`, `RemoteFrameCodec`)
Read by: a controller's `RemoteAgentConnection`; the agent's administrator, through its enrollments and audit

A remote agent is InterCat on a host whose administrator installed and enabled it to serve controllers - another
person's InterCat - that the administrator enrolled. Version 1.0 defines how the two know each other, how a controller
is enrolled, what an enrolled controller may ask, and how the agent refuses. It defines no capture, download or
command yet: what a request may ask for is closed, and every later request is added here before an agent answers it.

## 1. Parties and pins

Each party holds an ECDSA P-256 key in a self-signed certificate and is known by its **pin**: the SHA-256 of the key's
DER `SubjectPublicKeyInfo`, written as 64 lowercase hexadecimal digits. Neither party trusts a certificate authority,
a trust store, a certificate's names or its dates; no certificate is installed in any store. Two certificates of one key
have one pin; a new key is a new party.

## 2. Enrollment

1. The agent's administrator issues a **token** granting what one controller may ask (§5), lasting ten minutes unless
   the administrator says otherwise, and never more than an hour. The agent keeps the token's secret's SHA-256, its
   grants and when it lapses - never the secret.
2. The token's text is `icat-enroll-1.` and the unpadded base64url of: the version byte 1; the agent's pin, 32 bytes; a
   secret of 32 bytes from the system's random source; and a check, the first 4 bytes of the SHA-256 of the 65 bytes
   before it. It is copied whole, not typed; a token whose check does not match was cut short or altered.
3. The controller connects to the agent's endpoint expecting the token's pin (§3) and sends the secret in an `Enroll`
   request (§4.2) only once the agent's key is the one the pin names. A party between them never learns the secret.
4. The agent compares the secret's hash with every unexpired token's in full, and admits the controller's key - the
   pin of the certificate it presented - with that token's grants, consuming the token. A key already enrolled holds the
   new token's grants.
5. An unknown, used or expired secret is refused alike (`token-refused`), and a revoked key is refused whatever it
   presents (`revoked`); either refusal ends the connection.
6. The administrator revokes a key on the agent's host: its enrollment ends at once, its next request is refused, and
   no token enrolls it again. A controller whose key was revoked enrolls with a new key.

## 3. Transport

TCP to the endpoint the agent's administrator configured, and TLS 1.2 or 1.3 over it. The agent presents its certificate
and requires the controller's. The controller goes on only where the agent's key has the pin it expects - the token's,
or the one it enrolled with - and otherwise stops in the handshake, before any request: a wrong host's identity is
rejected. The agent admits any key to the handshake, so a controller can enroll; what a key may then ask, its pin
decides (§4).

A frame is a 32-byte header - the magic `ICRA`, the protocol's major version 1 and minor version 0, the message type,
its flags (request 1, response 2, error 4; a frame is a request or a response), the payload's length and a correlation
identifier - then the payload, at most 64 KiB, checked before anything is allocated for it. A payload is a run of
fields, each an identifier (uint16), a length (uint16, at most 16 KiB) and its bytes; at most 64 fields, none twice. A
reader passes over a field it does not know, so a later minor version may add fields. Integers are little-endian;
identifiers are 16 bytes, big-endian; text is UTF-8.

## 4. Messages

| Type | Name | Who may ask | Request fields | Response fields |
|---|---|---|---|---|
| 1 | Hello | any key | 1 major, 2 minor | 1 major, 2 minor, 3 enrolled; and only to an enrolled key, 4 the agent's host identity, 5 its grants (§5) |
| 2 | Enroll | any key not revoked | 1 the token's secret, 32 bytes | 1 the agent's host identity, 2 the grants now held |
| 3 | Inventory | an enrolled key | none | 1 host identity, 2 machine name, 3 operating system, 4 InterCat's version, each at most 256 bytes, 5 whether it serves a capture (version 1.0: no) |
| 255 | Error | the agent alone | - | 1 code (§6), 2 a message a person reads, at most 512 bytes |

A key not enrolled learns from hello the protocol and nothing of the host. No message names a command, a program, a
path or an address to act on: there is no remote shell, and a request of a type the agent does not answer is refused
(`unsupported`) without acting on it.

## 5. Grants and local policy

A token grants, each apart: `metadata-capture` (prepare, start, follow and stop a capture of metadata), `contents` (a
capture that keeps contents, never implied by metadata capture), `download` (fetch a finished session) and
`collectors` (install an optional collector). The agent's local policy is authoritative: a controller may do what it
was granted and the policy allows. By default the policy allows metadata capture and download; contents only where the
agent's administrator says so; collectors never, whatever is granted - installing one is an administrative workflow of
its own. Hello and enroll answer with the grants the policy leaves.

## 6. Refusals

| Code | Words | When | Then |
|---|---|---|---|
| 1 | `malformed` | a frame or a field version 1 would not write | the connection ends |
| 2 | `unsupported` | a message type or version the agent does not answer | the connection goes on |
| 3 | `not-enrolled` | a request from a key not enrolled | the connection goes on |
| 4 | `token-refused` | an unknown, used or expired secret | the connection ends |
| 5 | `revoked` | any request from a revoked key but hello | an enrollment's refusal ends the connection |
| 6 | `not-permitted` | a grant not held, or one the local policy forbids; no version 1.0 request asks one | the connection goes on |
| 7 | `busy` | reserved: version 1.0 closes a connection past its limit unanswered (§8) | - |

## 7. Audit

The agent records each request: when, the controller's pin, the message and its outcome. An entry never holds an
address, a port, a path or a command line (P16); a controller is named by its pin alone.

## 8. Limits

At most 8 connections at once - a connection past them is closed unanswered, before TLS - a handshake within 10
seconds, and a connection idle 60 seconds closed. One request is answered at a time on a connection.

## 9. Not defined at this version

The agent's host process, its installation and enablement; keeping keys, enrollments and the audit across restarts; a
key's rotation; captures, control leases and the broker on the agent's host; coordinated start and stop; store-and-forward
download with resume; partial-host investigations; the command line. Each is added to this contract before an agent
answers it.
