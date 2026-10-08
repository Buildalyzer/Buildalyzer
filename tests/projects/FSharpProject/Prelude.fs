// Declared last in the project file, but hoisted to the front by CompileOrder="CompileFirst".
// Everything below depends on it, so the project only compiles if that hoisting is honoured.
module Prelude

let exclaim (text: string) = text + "!"
