﻿using System.Collections.Generic;
using System.Net.Http;

namespace ZeroQL;

public class Unit
{
    public static Unit Default { get; } = new();
}

public interface IGraphQLResult
{
    string Query { get; }

    public GraphQueryError[]? Errors { get; }

    public Dictionary<string, object>? Extensions { get; }
    
    public HttpResponseMessage HttpResponseMessage { get; set; }
}

public class GraphQLResult<TData> : IGraphQLResult
{
    public GraphQLResult()
    {
        
    }
    
    public GraphQLResult(HttpResponseMessage responseMessage, string query, TData? data, GraphQueryError[]? errors, Dictionary<string, object>? extensions)
    {
        Query = query;
        Data = data;
        Errors = errors;
        Extensions = extensions;
        HttpResponseMessage = responseMessage;
    }

    public string Query { get; set; }

    public TData? Data { get; set; }

    public GraphQueryError[]? Errors { get; set; }

    public Dictionary<string, object>? Extensions { get; set; }

    public HttpResponseMessage HttpResponseMessage { get; set; }
}

public record GraphQLResponse<TData>
{
    public string Query { get; set; }
    
    public TData? Data { get; set; }

    public GraphQueryError[]? Errors { get; set; }
    
    public Dictionary<string, object>? Extensions { get; set; }
    
    public HttpResponseMessage HttpResponseMessage { get; set; }
}

public class GraphQueryError
{
    public string Message { get; set; }

    public object[] Path { get; set; }
    
    public Dictionary<string, object>? Extensions { get; set; }
}