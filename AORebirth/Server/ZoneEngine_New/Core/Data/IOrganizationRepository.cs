namespace ZoneEngine_New.Core.Data
{
    /// <summary>One organizations row (the Clan stat is its Id) with the leader's character name.</summary>
    public sealed record OrganizationRecord(
        int Id, System.DateTime Creation, string Name, int LeaderId, string LeaderName,
        int GovernmentForm, string Description, string Objective, string History);

    /// <summary>Read access to the organizations table.</summary>
    public interface IOrganizationRepository
    {
        OrganizationRecord? GetById(int organizationId);
    }
}
