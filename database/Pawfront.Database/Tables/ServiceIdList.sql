-- Table type used by sprocs that accept an array of ServiceIds (e.g. closure
-- batches). Sent over from .NET as a SqlParameter with TypeName 'Provider.ServiceIdList'.
CREATE TYPE [Provider].[ServiceIdList] AS TABLE
(
    [ServiceId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY
);
