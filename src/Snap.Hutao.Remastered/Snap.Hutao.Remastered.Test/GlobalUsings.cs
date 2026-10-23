global using Microsoft.VisualStudio.TestTools.UnitTesting;
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading.Tasks;
global using System.Threading;
global using System.Text.Json.Serialization;
global using System.Text.Json;
global using System.Diagnostics.CodeAnalysis;

// Linked production sources rely on the app's global usings; Sentry is provided by
// RuntimeBehavior/AgentSettingsTestBoundaries.cs instead of the real package.
global using Sentry;
