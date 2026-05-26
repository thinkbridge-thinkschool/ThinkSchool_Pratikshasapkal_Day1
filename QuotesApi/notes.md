# Day 8 — Piece 1 — Covering Index

## Before Plan
Execution plan showed:
- Index Seek
- Key Lookup
- Nested Loops

## Covering Index
```sql
CREATE INDEX IX_Quotes_Author_Covering
ON Quotes (Author)
INCLUDE (QuoteText, CreatedAt);
```

## After Plan
Key Lookup disappeared after using covering index.

## Logical Reads
logical reads 27