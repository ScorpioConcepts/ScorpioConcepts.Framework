namespace ScorpioConcepts.Framework.DBSchema
{
    /// <summary>
    /// The file formats a Database Schema can be stored in
    /// </summary>
    public enum DBSchemaFormat
    {
        /// <summary>
        /// JSON, the default for new schema files
        /// </summary>
        Json,
        /// <summary>
        /// The original XML format, still fully supported for existing schema files
        /// </summary>
        Xml
    }
}
