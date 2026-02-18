using System;

namespace AsyncRewriter.Neo4j;

public record Neo4JCredentials(Uri Url, string Username, string Password);