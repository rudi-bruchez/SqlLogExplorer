CREATE TABLE dbo.types (
  id int NOT NULL PRIMARY KEY,
  ti tinyint, si smallint, bi bigint, b1 bit, b2 bit, b3 bit,
  d9 decimal(9,2), d19 decimal(19,4), d28 decimal(28,6), d38 decimal(38,10), nu numeric(5,1),
  mo money, sm smallmoney, re real, fl float,
  dt date, t0 time(0), t3 time(3), t7 time(7), dtm datetime, sdt smalldatetime,
  d2_0 datetime2(0), d2_3 datetime2(3), d2_7 datetime2(7), dto datetimeoffset(7),
  g uniqueidentifier, ch char(5), vc varchar(20), nch nchar(3), nvc nvarchar(20),
  bn binary(4), vb varbinary(10), x xml);
INSERT dbo.types VALUES (1, 200, -12345, 1234567890123, 1, 0, 1,
  1234567.89, 123456789012345.6789, 1234567890123456789012.123456, 1234567890123456789012345678.0123456789, -1234.5,
  922337203685477.5807, -214748.3648, 3.5, -2.25,
  '2026-10-15', '13:14:15', '13:14:15.123', '13:14:15.1234567', '2026-10-15T13:14:15.123', '2026-10-15T13:14:00',
  '2026-10-15T13:14:15', '2026-10-15T13:14:15.123', '2026-10-15T13:14:15.1234567', '2026-10-15T13:14:15.1234567+02:00',
  '6F9619FF-8B86-D011-B42D-00C04FC964FF', 'ab', 'élan', N'日本', N'Grüße', 0xDEADBEEF, 0x0102, '<a/>');
INSERT dbo.types (id) VALUES (2);
INSERT dbo.types (id, vc, nvc) VALUES (3, '', N'');
