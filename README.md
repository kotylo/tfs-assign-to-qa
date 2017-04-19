# tfs-assign-to-qa
Reassign TFS workitems to QA, when pull request is merged to staging

# General Usage
You'll need to go through the App.config, set URL for TFS, your Project Name, and run this Console application.
It will monitor *staging* branch and get all TFS pull-requests for it.

Once the change has been found, item would be processed under **currently impersonated** user account. Processed means that your TFS work items structure is following:

1. PBI or BUG
1.1. **Child** Task for Developer. Assigned to Developer.
1.2. Child Task for QA. Assigned to QA.

So in history you'll see that you've modified this PBI:
1. Set the "Ready for Test" tag.
2. Reassigned to QA person (only if all child tasks were *Done*, except the single one for QA). This behavior can be altered, see [Advanced Assigning Rules](#advanced-assigning-rules).

## Advanced Assigning Rules
If you don't want to create Child tasks for QA, you can reassign PBI or Bug directly to QA, if you specify somewhere in Description that. You can use following phrase:

    Should be tested by <name>

Once you've changed Description of your workitem to contain this text, application will try to find this user. Please use DOMAIN\name format for the name.
However, if you don't like domain names, you can use following setting in App.config:

    <add key="customTesterNamesMapping" value="peter:DOMAIN\PETER"/>

All it adds is a mapping between username and domain name. So "Should be tested by peter." now also works.