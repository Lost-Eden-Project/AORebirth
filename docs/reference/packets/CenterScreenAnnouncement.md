# Center-screen announcements

This note is for the AORebirth client. It describes how an announcement reaches the client, from the zone server
connection down to the text, and how the retail client drew it. The new client draws its own version.

The retail client cannot show these messages. Its ApplySpells handler looks up the target character from a field
it never reads off the network, so it always finds nobody and does nothing. The new client has to handle the
message itself.

## When the server sends it

- **`.announce <text>`** sends one announcement to every online player. `.announce pf <text>` limits it to the
  GM's playfield, and `.announce me <text>` to the GM.
- **Shutdown countdown.** Every countdown line ("The server will shut down in 5 minutes."), the cancel line and
  the "shutting down now" line go out as an announcement. The same text is also sent as a normal chat line, so
  retail clients still see it. The new client may show both, or hide the chat copy.

More server features may use it later. Anything that needs the player's attention right now can go this way.

## How it arrives

An announcement is one N3 message, ApplySpells, sent on the normal zone server connection. The client reads it
the same way as every other zone message.

1. **The zone connection.** The client connects over TCP to the zone server address given at character login.
   Everything below travels on that one socket.
2. **Compression switch.** During zone login the server sends one plaintext InitiateCompression packet (packet type
   `0x7F00`). From the next byte on, everything the server sends is a single continuous zlib stream. The server
   flushes it after every packet, so each packet can be decoded as soon as it arrives. The client keeps one
   inflater for the whole session. Client-to-server traffic stays plaintext for the whole session.
3. **Packets.** Each decompressed packet starts with a 16-byte header. The size field gives the whole packet
   length, header included. Read the header, then read the remaining bytes.
4. **N3 messages.** A packet with type `0x000A` is an N3 message. Its body starts with a 4-byte N3 message type.
   ApplySpells is `0x342C1D1D`. Route on that value to the ApplySpells parser below.

A connection module can wrap the whole byte stream in its own encryption. With no module loaded the stream is
exactly as described here. Any module undoes its own layer before zlib.

### Packet header

All integers on the wire are big-endian.

| Offset | Field | Type | Value from the zone server |
| --- | --- | --- | --- |
| 0 | Packet counter | uint16 | Counts up from 1 for each compressed packet; `0xDFDF` on the plaintext InitiateCompression packet |
| 2 | Packet type | int16 | `0x000A` for N3 messages |
| 4 | Unknown | int16 | `0x0001` |
| 6 | Size | int16 | Whole packet length, header included |
| 8 | Sender | int32 | The playfield ID the client is on |
| 12 | Receiver | int32 | The player's character instance |

Plaintext packets are padded with zero bytes to a multiple of 4 bytes, and the padding is not counted in the size.
Only the InitiateCompression packet is affected, since everything after it is compressed. Compressed packets carry
no padding.

## The ApplySpells message

The body starts right after the 16-byte header. Strings are an int32 byte length followed by that many Latin-1
bytes, with no terminator.

| Field | Type | Value |
| --- | --- | --- |
| N3 message type | int32 | `0x342C1D1D` (ApplySpells) |
| Identity type | int32 | `50000`, the recipient character |
| Identity instance | int32 | Recipient character instance |
| N3 header byte | byte | `1` |
| Spell count | int32 | `(n + 1) * 0x3F1`; `0x7E2` for one spell |
| Spells | n times | See below |
| Execute | byte | `1` = run now; `0` = treat as a buff |

Each spell:

| Field | Type | Announcement value |
| --- | --- | --- |
| Function type | int32 | `53247` (`0xCFFF`) |
| Header 2 | int32 | `0` |
| Format version | int32 | `4` |
| Criteria count | int32 | `0`; each criterion would be three int32s |
| Stat 3 | int32 | `1` (hits) |
| Stat 4 | int32 | `0` (delay) |
| Stat 32 | int32 | `2` (target: self) |
| Stat 35 | int32 | `-1` |
| Text | string | The announcement, HTML allowed |
| Stat 11 | int32 | `0` (range: own character only) |

## Example

The text `Hello` to character 50000:12345 on playfield 4582, after zlib decoding. The packet is 79 bytes: a
16-byte header and a 63-byte body. The first two bytes are the packet counter, `0xDFDF` here as built before
sending. On the wire they hold the current counter value.

```
df df 00 0a 00 01 00 4f 00 00 11 e6 00 00 30 39   header: counter, N3, 1, size 79, pf 4582, receiver 12345
34 2c 1d 1d 00 00 c3 50 00 00 30 39 01 00 00 07   ApplySpells, 50000:12345, 1, count 0x7E2 ...
e2 00 00 cf ff 00 00 00 00 00 00 00 04 00 00 00   ... 53247, header2 0, version 4, criteria 0 ...
00 00 00 00 01 00 00 00 00 00 00 00 02 ff ff ff   ... stat3 1, stat4 0, stat32 2, stat35 -1 ...
ff 00 00 00 05 48 65 6c 6c 6f 00 00 00 00 01      ... length 5, "Hello", stat11 0, execute 1
```

## Parsing rules

1. Reject the spell count unless it is positive and a multiple of `0x3F1`. Retail also rejects 1000 or more spells.
2. Only function type 53247 with version 4 is defined for the new client. Each function type has its own argument
   list, so an unknown type cannot be skipped. Stop at the first unknown spell and ignore the rest of the message.
3. Show the text only when Execute is nonzero. Retail skipped the announcement when the spells were treated as a
   buff.
4. Show it when range is 0, as the server always sends. Retail also showed it to a nearby player when range was
   above 0 and that player was within range of the target. The server does not use that today.
5. Cap the text length on your side as well. The server sends at most 1024 characters.

## How retail drew it

These details come from the retail GUI.dll and the default prefs. Retail was never seen showing server-sent text,
so treat them as a starting point.

- **Placement.** A frameless, see-through window across the top of the screen. Its default frame is the full
  screen width and the top third of its height. Players could move it, and the Options panel had a checkbox to give
  it a frame.
- **Lines.** Each announcement becomes a new line added below the earlier ones. Each line fades out on its own,
  apparently after about 5 seconds with a 3 second fade. When the lines no longer fit, the oldest is dropped.
- **Text.** Lines are HTML in the AO text dialect: `<font color=#rrggbb>`, `<img src=...>`, and item links all
  work. The retail tutorial lines used centered text with an icon and coloured keywords.
- **On or off.** A "Notification Window" option turned the whole window on or off. It was on by default.

Retail only ever used this for 13 ICC Shuttleport tutorial reminder nanos, such as 292321, "Heal Mission Reminder".
Their text is in the client database if you want real examples to test with.

## Server source

- Message builder: `AORebirth/Server/ZoneEngine_New/Core/Helpers/CenterScreenAnnouncement.cs`
- Wire format: `ApplySpellsMessageSerializer.cs` in AOtomation.Messaging
- Packet header and zlib stream: `AORebirth/Server/ZoneEngine_New/Core/Network/ZoneSession.cs` and
  `ZoneMessageCodec.cs`
