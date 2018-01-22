# Description
Reassign TFS workitems to QA, when pull request is merged to testing branch (staging/production).

Also adds *Ready for Test* tag for this PBI/Bug, so that QA is ready to test.

# Version history
## v1.2
- Added comment support
- More colors in Console (highlight title)
- Tracking child tasks. There should not be *In Progress* Dev tasks when Pull Request completes.
- Figuring out who created a Bug for you and if he's in QA list (defined in configuration), assigning child QA task to him/her.

## v1.1
- When there is one task with activity Development, and it's not Done, that means user is still working on the item and it's not marked with Ready For Test tag.
- Added creation of Tasks for specified users (createTasksUsersList property in config)
- Who created the PBI is automatically becoming the one having QA Task (in the new functionality above)
- Monitoring for multiple repositories / branches

# General Usage
You'll need to go through the App.config, set URL for TFS, your Project Name, and run this Console application.
It will monitor *staging* branch and get all TFS pull-requests for it.

Once the change has been found, item would be processed under **currently impersonated** user account. Processed means that your TFS work items structure is following:
- PBI or BUG
  - **Child** Task for Developer. Assigned to Developer.
  - Child Task for QA. Assigned to QA.

So in history you'll see that you've modified this PBI:
1. Set the "Ready for Test" tag.
2. Reassigned to QA person (only if all child tasks were *Done*, except the single one for QA). This behavior can be altered, see [Advanced Assigning Rules](#advanced-assigning-rules).

# Screenshot in action
![Screenshot of application](/dist/screenshots/assign-to-qa-screenshot-01.png?raw=true)

# Auto creation of Dev/QA tasks
If you want the structure, described in the [General Usage](#general-usage) above, to be automatically recreated each time somebody reassignes PBI/Bug to you with empty tasks, it's possible with **createTasksUsersList** property. Just specify usernames (not everybody in the team may want to agree to this for some reason) who will have their Dev and QA tasks automatically recreated, and application will do that for you every *updateIntervalMinutes* minutes.

In this case, user who created the Bug will be Assigned automatically in QA Task.  

## Advanced Assigning Rules

If you don't want to create Child tasks for QA, you can reassign PBI or Bug directly to QA, if you specify somewhere in Description that. You can use following phrase:

    Should be tested by <name>

Once you've changed Description of your workitem to contain this text, application will try to find this user. Please use DOMAIN\name format for the name.
However, if you don't like domain names, you can use following setting in App.config:

    <add key="customTesterNamesMapping" value="peter:DOMAIN\PETER"/>

All it adds is a mapping between username and domain name. So "Should be tested by peter." now also works.